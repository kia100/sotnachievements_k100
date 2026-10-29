## Modifying / Adding Achievements

Texts for achievements are stored in [`assets/achievements.json`](assets/achievements.json). The formatting is as bellow:

```
{
    "id": "",
    "badge": "",
    "title": "",
    "desc": "",
    "done": "",
    "condition": "",
    "memaddr": ""
  },
```

Explanation:

`id`: Achievement ID. A number written as a string.

`badge`: The number used for loading the required image files. For instance, if the number is x (written in string formatting), the mod will look for x.png and x_lock.png, for unlocked and locked states respectively.

`title`: Title of the achievement.

`desc`: Description for locked state.

`done`: Description for unlocked state. The mod will use `desc` if `done` is an empty string ("").

`condition` : A small retroactive check syntax. Optional.

`memaddr`: The raw RA logic used for live detection
