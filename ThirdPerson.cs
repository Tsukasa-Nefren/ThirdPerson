using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Sharp.Shared;
using Sharp.Shared.CStrike;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameObjects;
using Sharp.Shared.HookParams;
using Sharp.Shared.Hooks;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace ThirdPerson;

public unsafe class ThirdPerson : IModSharpModule, IGameListener, IClientListener, IEventListener
{
    public string DisplayName => "ThirdPerson";
    public string DisplayAuthor => "Tsukasa";

    private const string CameraClassname = "custom_player_camera";
    private const byte ModeFollowPosition = 3;
    private const string GameDataFile = "ThirdPerson.games.jsonc";

    private static int _eyePositionVfuncIndex;
    private static int _cameraPawnHandleOffset;

    public ThirdPerson(ISharedSystem sharedSystem, string dllPath, string sharpPath, Version version, IConfiguration coreConfiguration, bool hotReload)
    {
        _modSharp = sharedSystem.GetModSharp();
        _clients = sharedSystem.GetClientManager();
        _entities = sharedSystem.GetEntityManager();
        _events = sharedSystem.GetEventManager();
        _hooks = sharedSystem.GetHookManager();
        _configPath = Path.Combine(sharpPath ?? string.Empty, "configs", "ThirdPerson", "config.jsonc");
    }

    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly IEntityManager _entities;
    private readonly IEventManager _events;
    private readonly IHookManager _hooks;
    private readonly string _configPath;
    private Options _options = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class Options
    {
        public bool Enabled { get; set; } = true;
        public float Distance { get; set; } = 150f;
        public float Side { get; set; } = 0f;
        public float Up { get; set; } = 0f;
        public bool Clip { get; set; } = false;
        public float ReturnStrength { get; set; } = 1f;
    }

    private void LoadOptions()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                File.WriteAllText(_configPath, JsonSerializer.Serialize(_options, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            var parsed = JsonSerializer.Deserialize<Options>(File.ReadAllText(_configPath), JsonOptions);
            if (parsed != null)
                _options = parsed;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ThirdPerson] Failed to load {_configPath}: {ex.Message}");
        }
    }

    private readonly bool[] _enabled = new bool[PlayerSlot.MaxPlayerCount];
    private readonly IBaseEntity?[] _cameras = new IBaseEntity?[PlayerSlot.MaxPlayerCount];
    private readonly uint[] _pawnHandles = new uint[PlayerSlot.MaxPlayerCount];

    private static IEntityManager? _entitiesStatic;
    private static readonly Dictionary<uint, nint> _pawnHandleToPawnPtr = [];
    private static IRuntimeNativeHook? _eyeHook;
    private static nint _eyeTrampoline;

    public bool Init()
    {
        _entitiesStatic = _entities;

        try
        {
            var gameData = _modSharp.GetGameData();
            gameData.Register(GameDataFile);
            _eyePositionVfuncIndex = gameData.GetVFuncIndex("ThirdPerson.CCSCustomPlayerCamera", "GetEyePosition");
            _cameraPawnHandleOffset = gameData.GetOffset("ThirdPerson.CCSCustomPlayerCamera", "m_hPawn");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ThirdPerson] gamedata incomplete: {ex.Message}");
            return false;
        }

        _modSharp.InstallGameListener(this);
        _clients.InstallClientListener(this);
        _events.InstallEventListener(this);
        _events.HookEvent("player_spawned");
        _events.HookEvent("player_death");
        _clients.InstallCommandCallback("tp", OnCommandToggle);
        _clients.InstallCommandCallback("thirdperson", OnCommandToggle);
        return true;
    }

    public void PostInit() { }
    public void OnAllModulesLoaded() { }
    public void OnLibraryConnected(string name) { }
    public void OnLibraryDisconnect(string name) { }

    public void Shutdown()
    {
        foreach (var camera in _cameras)
            if (camera is { } cam && cam.IsValid())
                cam.Kill();
        Array.Clear(_cameras);
        Array.Clear(_enabled);
        Array.Clear(_pawnHandles);

        _modSharp.RemoveGameListener(this);
        _clients.RemoveClientListener(this);
        _events.RemoveEventListener(this);
        _clients.RemoveCommandCallback("tp", OnCommandToggle);
        _clients.RemoveCommandCallback("thirdperson", OnCommandToggle);

        _eyeHook?.Uninstall();
        _eyeHook = null;
        _eyeTrampoline = nint.Zero;
        _pawnHandleToPawnPtr.Clear();
        _entitiesStatic = null;

        try
        {
            _modSharp.GetGameData().Unregister(GameDataFile);
        }
        catch (Exception) { }
    }

    private ECommandAction OnCommandToggle(IGameClient client, StringCommand command)
    {
        if (!client.IsValid)
            return ECommandAction.Stopped;

        LoadOptions();

        if (!_options.Enabled)
            return ECommandAction.Stopped;

        var controller = client.GetPlayerController();
        if (controller == null)
            return ECommandAction.Stopped;

        if (_enabled[client.Slot])
            Disable(client.Slot, controller);
        else
            Enable(client.Slot, controller);

        return ECommandAction.Stopped;
    }

    private void Enable(PlayerSlot slot, IPlayerController controller, bool notify = true)
    {
        if (!controller.IsValid())
        {
            _enabled[slot] = false;
            _cameras[slot] = null;
            return;
        }

        var pawn = controller.GetPawn();
        if (pawn == null || !pawn.IsAlive)
        {
            if (notify)
                controller.Print(HudPrintChannel.Chat, " [TP] You must be alive to enable third person");
            return;
        }

        RemoveCamera(slot);

        var camera = _entities.CreateEntityByName(CameraClassname);
        if (camera == null)
        {
            controller.Print(HudPrintChannel.Chat, " [TP] Failed to create camera");
            return;
        }

        uint pawnHandle = pawn.Handle.GetValue();
        camera.DispatchSpawn();
        camera.Teleport(pawn.GetAbsOrigin(), null, null);
        camera.SetNetVar("m_hFollowEntity", pawnHandle);
        camera.SetNetVar("m_bFollowEyes", true);
        camera.SetNetVar("m_vecFollowOffset", new Vector(0f, 0f, 0f));
        camera.SetNetVar("m_vecCameraOffset", new Vector(-_options.Distance, _options.Side, _options.Up));
        camera.SetNetVar("m_bClipCameraOffset", _options.Clip);
        camera.SetNetVar("m_flCameraOffsetReturnStrength", _options.ReturnStrength);
        camera.SetNetVar("m_hPawn", pawnHandle);
        camera.SetNetVar("m_nCameraMode", ModeFollowPosition);
        pawn.GetCameraService()?.ViewEntity = camera;

        _cameras[slot] = camera;
        _enabled[slot] = true;
        _pawnHandles[slot] = pawnHandle;
        EnsureEyeHookInstalled(camera.GetAbsPtr(), pawnHandle, pawn.GetAbsPtr());
        controller.Print(HudPrintChannel.Chat, " [TP] Third person enabled");
    }

    private void Disable(PlayerSlot slot, IPlayerController controller)
    {
        if (controller.IsValid() && controller.GetPawn() is { } pawn)
            pawn.GetCameraService()?.ViewEntity = null;

        RemoveCamera(slot);
        _enabled[slot] = false;
        controller.Print(HudPrintChannel.Chat, " [TP] Third person disabled");
    }

    private void RemoveCamera(PlayerSlot slot)
    {
        _pawnHandleToPawnPtr.Remove(_pawnHandles[slot]);
        _pawnHandles[slot] = 0;
        if (_cameras[slot] is { } camera)
        {
            if (camera.IsValid())
                camera.Kill();
            _cameras[slot] = null;
        }
    }

    public void FireGameEvent(IGameEvent @event)
    {
        var name = @event.GetName();

        if (name == "player_death")
        {
            var dead = @event.GetPlayerController("userid");
            if (dead != null && _enabled[dead.PlayerSlot])
            {
                if (dead.IsValid() && dead.GetPawn() is { } deadPawn)
                    deadPawn.GetCameraService()?.ViewEntity = null;
                RemoveCamera(dead.PlayerSlot);
            }
            return;
        }

        if (name != "player_spawned")
            return;

        var controller = @event.GetPlayerController("userid");
        if (controller == null || !_enabled[controller.PlayerSlot])
            return;

        var slot = controller.PlayerSlot;
        _modSharp.PushTimer(() =>
        {
            if (_enabled[slot] && controller.IsValid())
                Enable(slot, controller, notify: false);
        }, 0.2);
    }

    public bool HookFireEvent(IGameEvent @event, ref bool serverOnly) => true;

    public void OnRoundRestarted()
    {
        foreach (var controller in _entities.GetPlayerControllers(true).ToArray())
        {
            if (!_enabled[controller.PlayerSlot])
                continue;

            var slot = controller.PlayerSlot;
            _modSharp.PushTimer(() =>
            {
                if (_enabled[slot] && controller.IsValid())
                    Enable(slot, controller, notify: false);
            }, 0.5);
        }
    }

    public void OnClientPutInServer(IGameClient client) { }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        _enabled[client.Slot] = false;
        _cameras[client.Slot] = null;
        _pawnHandles[client.Slot] = 0;
    }

    public void OnGameActivate()
    {
        Array.Clear(_cameras);
        Array.Clear(_pawnHandles);
    }

    public void OnGameDeactivate()
    {
        Array.Clear(_cameras);
        Array.Clear(_enabled);
        Array.Clear(_pawnHandles);
    }

    int IGameListener.ListenerVersion => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;
    int IClientListener.ListenerVersion => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;
    int IEventListener.ListenerVersion => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

    private void EnsureEyeHookInstalled(nint cameraPtr, uint pawnHandle, nint pawnPtr)
    {
        _pawnHandleToPawnPtr[pawnHandle] = pawnPtr;
        if (_eyeHook != null)
            return;

        var cameraVtable = Marshal.ReadIntPtr(cameraPtr);
        var hook = _hooks.CreateVirtualHook();
        hook.Prepare(cameraVtable, _eyePositionVfuncIndex, (nint)(delegate* unmanaged<nint, nint, nint>)&HookGetEyePosition);
        if (!hook.Install())
            return;

        _eyeTrampoline = hook.Trampoline;
        _eyeHook = hook;
    }

    [UnmanagedCallersOnly]
    private static nint HookGetEyePosition(nint self, nint outVec)
    {
        if (_entitiesStatic is { } entities)
        {
            uint pawnHandle = *(uint*)(self + _cameraPawnHandleOffset);
            if (pawnHandle != 0xFFFFFFFF
                && _pawnHandleToPawnPtr.TryGetValue(pawnHandle, out var pawnPtr)
                && pawnPtr != nint.Zero)
            {
                var pawn = entities.MakeEntityFromPointer<IBaseModelEntity>(pawnPtr);
                if (pawn != null && pawn.IsValid())
                {
                    var origin = pawn.GetAbsOrigin();
                    var viewOffset = pawn.ViewOffset;
                    var eyes = (float*)outVec;
                    eyes[0] = origin.X + viewOffset.X;
                    eyes[1] = origin.Y + viewOffset.Y;
                    eyes[2] = origin.Z + viewOffset.Z;
                    return outVec;
                }
            }
        }

        return _eyeTrampoline != nint.Zero
            ? ((delegate* unmanaged<nint, nint, nint>)_eyeTrampoline)(self, outVec)
            : outVec;
    }
}
