using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class GameDetection
    {
        public GameProfile Profile;

        public int RendererPid;

        public long RendererCreation;

        public string RendererName;

        public string RendererPath;

        public bool RendererForeground;

        public bool RendererCandidateSelected;

        public bool RendererUserSelected;

        public bool RendererLearnable;

        public bool RendererSafetyOnly;

        public bool RequiresGpuConfirm;

        public long RendererGpuProofExpiresMs;

        public int RendererMatchRank;

        public readonly HashSet<string> FamilyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public readonly HashSet<int> FamilyPids = new HashSet<int>();

        public string Evidence;
    }
}
