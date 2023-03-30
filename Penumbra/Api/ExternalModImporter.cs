using Dalamud.Game.ClientState.Keys;
using OtterGui.Filesystem;
using OtterGui.FileSystem.Selector;
using Penumbra.Mods;
using Penumbra.UI.Classes;
using System;
using System.IO;
using System.Linq;

namespace Penumbra.Api {
    public class ExternalModImporter {
        private static ModFileSystemSelector instance;

        public static ModFileSystemSelector Instance { get => instance; set => instance = value; }

        public static void UnpackMod( string modPackagePath )
        {
            instance.AddStandaloneMod( modPackagePath );
        }
    }
}
