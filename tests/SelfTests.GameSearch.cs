#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunGameSearchRegressionTests()
        {
            Action[] tests =
            {
                GameSearchEmptyAndMissingFields, GameSearchChineseFragments,
                GameSearchPunctuationAndCase, GameSearchCompatibleUnicode,
                GameSearchTermsAcrossFields, GameSearchLiteralPunctuationAndPaths,
                GameSearchMalformedUnicode
            };
            foreach (Action test in tests) test();
            return tests.Length;
        }

        private static void GameSearchEmptyAndMissingFields()
        {
            Eq(true, GameSearchMatcher.Matches(null, null, null));
            Eq(true, GameSearchMatcher.Matches("", "Anything", null));
            Eq(true, GameSearchMatcher.Matches(" \t\r\n\u3000", null, ""));
            Eq(false, GameSearchMatcher.Matches("missing", null, null));
            Eq(true, GameSearchMatcher.Matches("entry", null, @"D:\Games\Entry.exe"));
        }

        private static void GameSearchChineseFragments()
        {
            Eq(true, GameSearchMatcher.Matches("悟空", "黑神话：悟空", @"D:\Library\Game.exe"));
            Eq(true, GameSearchMatcher.Matches("黑 神 话", "黑神话：悟空", ""));
            Eq(true, GameSearchMatcher.Matches("黑神话悟空", "黑神话：悟空", ""));
            Eq(false, GameSearchMatcher.Matches("悟空续作", "黑神话：悟空", ""));
        }

        private static void GameSearchPunctuationAndCase()
        {
            const string name = "Baldur’s Gate 3";
            Eq(true, GameSearchMatcher.Matches("baldurs gate", name, ""));
            Eq(true, GameSearchMatcher.Matches("BALDURS-GATE3", name, ""));
            Eq(true, GameSearchMatcher.Matches("baldur's gate", name, ""));
            Eq(true, GameSearchMatcher.Matches("baldursgate", name, ""));
            Eq(true, GameSearchMatcher.Matches("baldurs --- gate", name, ""));
            Eq(false, GameSearchMatcher.Matches("baldurs gate 4", name, ""));
        }

        private static void GameSearchCompatibleUnicode()
        {
            Eq(true, GameSearchMatcher.Matches("ＢＡＬＤＵＲＳ　ＧＡＴＥ　３", "Baldur’s Gate 3", ""));
            Eq(true, GameSearchMatcher.Matches("baldurs gate 3", "Ｂａｌｄｕｒ’ｓ　Ｇａｔｅ　３", ""));
            Eq(true, GameSearchMatcher.Matches("ＣＬＩＥＮＴ．ＥＸＥ", null, @"D:\Games\Client.exe"));
            Eq(true, GameSearchMatcher.Matches("Cafe\u0301", "Café", ""));
        }

        private static void GameSearchTermsAcrossFields()
        {
            const string name = "Baldur’s Gate 3";
            const string path = @"D:\Library\Release\Binaries\Win64\bg3.exe";
            Eq(true, GameSearchMatcher.Matches("gate baldurs", name, path));
            Eq(true, GameSearchMatcher.Matches("gate win64 bg3", name, path));
            Eq(true, GameSearchMatcher.Matches("release library", name, path));
            Eq(false, GameSearchMatcher.Matches("gate win32 bg3", name, path));
            Eq(false, GameSearchMatcher.Matches("gate missing", name, path));
            Eq(false, GameSearchMatcher.Matches("NamePath", "Name", "Path"));
        }

        private static void GameSearchLiteralPunctuationAndPaths()
        {
            Eq(false, GameSearchMatcher.Matches("---", "Ordinary Game", @"D:\Library\Game.exe"));
            Eq(true, GameSearchMatcher.Matches("---", "Game --- Special", ""));
            Eq(false, GameSearchMatcher.Matches("!!!", null, null));
            Eq(true, GameSearchMatcher.Matches("：", "Game: Special", ""));
            Eq(true, GameSearchMatcher.Matches(@"\", "Game", @"D:\Library\Game.exe"));
            Eq(false, GameSearchMatcher.Matches("/", "Game", @"D:\Library\Game.exe"));
            Eq(true, GameSearchMatcher.Matches(@"d:\games\my-game", "", @"D:\Games\My Game\Binaries\Entry.exe"));
            Eq(true, GameSearchMatcher.Matches("D:/Games/My-Game", "", @"D:\Games\My Game\Binaries\Entry.exe"));
            Eq(true, GameSearchMatcher.Matches("entry.exe", "", @"D:\Games\My Game\Binaries\Entry.exe"));
        }

        private static void GameSearchMalformedUnicode()
        {
            Eq(false, GameSearchMatcher.Matches("\uD800", "Game", null));
            Eq(false, GameSearchMatcher.Matches("\uDC00", null, ""));
            Eq(true, GameSearchMatcher.Matches("game", "\uD800Game", "\uDC00"));
            Eq(true, GameSearchMatcher.Matches("\uD800", "\uD800", null));
        }
    }
}
#endif
