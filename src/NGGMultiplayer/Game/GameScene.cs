namespace NGGMultiplayer.Game
{
    /// <summary>The game's scenes (build order 0 INTRO, 1 Menu2, 2 Main, 3 CREDITS). Values go on the wire.</summary>
    public enum GameScene : byte
    {
        None = 0,
        Intro = 1,
        Menu = 2,
        Main = 3,
        Credits = 4,
        Other = 5
    }

    public static class GameScenes
    {
        public const string IntroName = "INTRO";
        public const string MenuName = "Menu2";
        public const string MainName = "Main";
        public const string CreditsName = "CREDITS";

        public static GameScene FromName(string name)
        {
            switch (name)
            {
                case IntroName: return GameScene.Intro;
                case MenuName: return GameScene.Menu;
                case MainName: return GameScene.Main;
                case CreditsName: return GameScene.Credits;
                case null:
                case "": return GameScene.None;
                default: return GameScene.Other;
            }
        }

        /// <summary>The scene name to load, or null for scenes the mod never loads itself.</summary>
        public static string ToName(GameScene scene)
        {
            switch (scene)
            {
                case GameScene.Intro: return IntroName;
                case GameScene.Menu: return MenuName;
                case GameScene.Main: return MainName;
                case GameScene.Credits: return CreditsName;
                default: return null;
            }
        }
    }
}
