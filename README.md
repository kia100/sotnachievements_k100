# Achievements

An achievements mod for [SymphonyRecomp](https://github.com/BlackLabelHQ/SymphonyRecomp), the Castlevania: Symphony of the Night PSX recomp.

It puts the whole RetroAchievements SotN set (106 achievements) right in the game, with pop-ups when you earn one and a menu to look through them. It's all offline, there's no RetroAchievements login or connection involved, the logic just ships with the mod and watches the game's memory.

## What it does

- All 106 achievements, with the titles, descriptions and badges from the RA set.
- When you unlock one, a little Symphony-style banner slides in and the badge pops up over Alucard's head.
- An "Achievements" entry on the top menu bar opens a window with the full list, locked and unlocked, plus a count.
- If you load an old save it'll unlock everything you've already done, at least the stuff that can be read back from the save (bosses, level, relics, key items, warps and so on).
- The rest (damageless runs, kill streaks, that kind of thing) can't be told from a save, so those just pop the moment you actually do them. That part runs the real RA condition logic.

## Install

1. Grab the latest release zip, and unzip it.
2. Drop the folder in your SymphonyRecomp `mods` folder so you get `mods/sotnachievements/`.
3. Start the game and open the Achievements menu.

## Notes

- Nothing talks to RetroAchievements. The conditions and badges are bundled and everything runs locally.
- Retroactive only works for stuff that sticks in your save. A past damageless fight can't be proven from a save file, so those need a fresh run.
- There's a "Reset all achievements" button under `Mods > Achievements` if you want to test again. They just re-unlock next time the condition is met.

## Tweaking / adding achievements

See [EDITING.MD](EDITING.MD) for a comprehensive documentation.

## Credits

- The achievement set itself (design, titles, descriptions, badge art) is the RetroAchievements Symphony of the Night set, [SotN](https://retroachievements.org/game/11240), by its authors.
- SymphonyRecomp is by BlackLabelHQ.
- Mod by mxd.

The badges and achievement text belong to RetroAchievements and the set's authors. They are included so the mod matches that set. See LICENSE for the details.
