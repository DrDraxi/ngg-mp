# NGG map 1/4: install facts
- Install: C:\Program Files (x86)\Steam\steamapps\common\Normal Golf Game\Normal\ (extra "Normal" subfolder; junk __MACOSX at top). Exe `Normal Golf Game.exe`, data `Normal Golf Game_Data`. BepInEx goes next to the exe.
- Mono. Unity 6000.3.11f1. PhysX 4.1.2. New Input System 1.19 (+ legacy Input in places). D3D11.
- Steamworks.NET 2025.164.0 (SDK 1.64) shipped as Managed\com.rlabrecque.steamworks.net.dll (assembly version 0.0.0.0). steam_api64.dll in Plugins\x86_64. AppId 3510740 hardcoded in SteamManager.
- Game assemblies: Assembly-CSharp (608 KB), Plugins, ECM2 (Easy Character Movement 2), EasyWallCollider, GrassFlow, MicroSplat, Febucci TextAnimator, DOTween; Unity Cinemachine, Timeline, InputSystem, TextMeshPro, ProBuilder, Splines, VisualScripting, AI.Navigation, URP. mscorlib 4.x profile.
- Saves: %USERPROFILE%\AppData\LocalLow\Luke Muscat\Normal Golf Game\ : run2.txt (+run2_temp.txt), actuallyNormal.txt, scoreCard.txt, unlocks.txt, config.txt; JSON via JsonUtility; steam_autocloud.vdf => Steam Cloud syncs the folder. Registry only has Unity screen prefs.
- Build string "Build 2:45PM September 29".
