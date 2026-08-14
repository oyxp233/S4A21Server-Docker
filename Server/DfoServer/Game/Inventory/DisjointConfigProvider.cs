using DfoServer.GameWorld;
using PvfLib;
using System;

namespace DfoServer.Game.Inventory
{
    internal static class DisjointConfigProvider
    {
        private static readonly Lazy<DisjointFile> SystemDisjoint =
            new Lazy<DisjointFile>(() => DisjointFile.Parse(PvfArchiveAccessor.ReadText("etc/disjoint.etc")));

        public static DisjointFile LoadSystemDisjoint()
        {
            return SystemDisjoint.Value;
        }
    }
}
