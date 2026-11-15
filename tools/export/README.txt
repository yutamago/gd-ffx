gd-ffx
======

A Godot frontend for ffx-rust, the Rust rewrite of FINAL FANTASY X (the
Steam HD Remaster). https://github.com/yutamago/gd-ffx

You need your own copy of the game
----------------------------------

gd-ffx contains nothing of FINAL FANTASY X: no game files, no extracted or
converted assets. It reads the game from your installed copy of FINAL
FANTASY X/X-2 HD Remaster (Steam) when it runs, and only reads it. Tell it
where the game is (the folder with FFX.exe and data/), the first found of:

  1. on the command line, after "--":
       Linux:    ./gd-ffx.x86_64 -- --game-dir="/path/to/FINAL FANTASY FFX&FFX-2 HD Remaster"
       Windows:  gd-ffx.exe -- --game-dir="C:\path\to\FINAL FANTASY FFX&FFX-2 HD Remaster"
  2. the environment variable FFX_GAME_DIR;
  3. a file override.cfg beside the program with:
       [ffx]
       game/game_dir="/path/to/the/game"

Your saves and the game's settings are left alone: gd-ffx keeps its own
(GameSetting.ini, saves) and the maps and characters it imports from your
copy in its user data folder (Linux: ~/.local/share/godot/app_userdata/gd-ffx,
Windows: %APPDATA%\Godot\app_userdata\gd-ffx).

Keys: arrows walk, C/Enter confirm, X/Backspace cancel, V menu, B Start,
F1 the debug overlay, Esc the PC menu. Pads work as the game's XInput pads.

What is here
------------

  gd-ffx.x86_64 / gd-ffx.exe   the program (Godot, with the project inside)
  gd-ffx.console.exe           Windows: the same with a console (log output)
  libffx_gd.so / ffx_gd.dll    ffx-rust, the game's logic, as a Godot extension
  data_gd-ffx_*                the C# part and the .NET runtime
  licences/                    gd-ffx's licence and the third-party notices

Linux (and the Steam Deck): x86_64, glibc 2.34 or newer.

FINAL FANTASY is a registered trademark of Square Enix Holdings Co., Ltd.
gd-ffx is a fan project, not affiliated with or endorsed by Square Enix.
gd-ffx's code is licensed under MIT OR Apache-2.0 (licences/).
