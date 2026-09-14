# ThirdPerson

Third-person toggle for CS2 (ModSharp).

## Usage

`!tp` / `!thirdperson`

## Configuration

`sharp/configs/ThirdPerson/config.jsonc` — re-read on every toggle.

| Key | Default | Description |
|---|---|---|
| `enabled` | true | Enable the module |
| `distance` | 150 | Distance behind the player |
| `side` / `up` | 0 | Lateral / vertical offset |
| `clip` | false | Clip against walls |
| `returnStrength` | 1.0 | Return smoothing (0-1) |

## Install

Extract `ThirdPerson.zip` from the latest Actions run into the game's `game/` directory. A fresh install needs a server restart; updates go to `sharp/modules/ThirdPerson/reload/`.
