// @author bdth 2074055628@qq.com
// 文件用途 游戏列表 配置档案与白名单的持久化操作
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private const string WhitelistFooterPrefix = "PAVISE_WHITELIST_END|";
        private string whitelistLastError = "";

        public string WhitelistLastError
        {
            get { lock (sync) return whitelistLastError; }
        }

        public bool AddGameExecutable(string name, string executablePath)
        {
            string error;
            return AddGameExecutableCore(name, executablePath, null, true, out error);
        }

        private bool AddGameExecutableCore(string name, string executablePath,
            string preferredRoot, bool persist, out string error)
        {
            string resolved, suggestedName;
            if (!GameExecutableResolver.TryResolve(executablePath, out resolved, out error, out suggestedName))
                return false;
            string entry = StripExe(Path.GetFileName(resolved));
            string display = DisplayName(resolved,
                string.IsNullOrWhiteSpace(name) ? suggestedName : name);
            string root = null;
            if (!string.IsNullOrEmpty(preferredRoot))
            {
                string normalized = NormalizeGameRoot(preferredRoot);
                if (normalized != null && FamilyBoundary.UnderRoot(resolved, normalized)) root = normalized;
            }
            if (root == null) root = NormalizeGameRoot(GameScan.InferGameRoot(resolved));
            root = ResolveLibraryInstallRoot(resolved, root);
            lock (sync)
            {
                if (stopping) return false;
                if (autoAddIgnore.Remove(resolved) && !SaveAutoIgnoreLocked()) return false;
                foreach (GameProfile p in profiles)
                {
                    if (string.Equals(p.ExecutablePath, resolved, StringComparison.OrdinalIgnoreCase)) return false;
                    if (string.Equals(p.LearnedExecutablePath, resolved, StringComparison.OrdinalIgnoreCase)) return false;
                    if (string.IsNullOrEmpty(p.ExecutablePath) && p.Entries.Contains(entry))
                    {
                        p.ExecutablePath = resolved;
                        p.Root = root;
                        p.Name = display;
                        p.LearnedExecutablePath = null;
                        if (persist && !PersistLibraryLocked()) return false;
                        if (persist) KickLibraryChanged();
                        return true;
                    }
                }
                GameProfile profile = GameProfileStore.NewProfile(display, root, resolved);
                profile.Entries.Clear();
                profile.Entries.Add(entry);
                profiles.Add(profile);
                if (persist && !PersistLibraryLocked()) return false;
            }
            if (persist) KickLibraryChanged();
            return true;
        }

        private bool PersistLibraryLocked()
        {
            return SaveProfilesLocked();
        }

        public event Action LibraryChanged;

        private void RaiseLibraryChanged()
        {
            Action handler = LibraryChanged;
            if (handler != null) { try { handler(); } catch { } }
        }

        private void KickLibraryChanged()
        {
            ClearFamilyDiscovery();
            InvalidateRendererHandoff();
            InvalidateFamilyPolicy();
            RequestFullGameDetection();
            RequestPolicyApply();
            RaiseLibraryChanged();
        }

        public bool AddGameFile(string selectedPath, out string error)
        {
            string executable;
            if (!GameExecutableResolver.TryResolve(selectedPath, out executable, out error)) return false;
            if (!AddGameExecutable(null, executable))
            {
                error = Lang.T("t.gamemodelibrary.1");
                return false;
            }
            error = null;
            return true;
        }

        public int AddScannedGames(IList<ScanHit> hits, out string lastError)
        {
            lastError = null;
            int added = 0;
            if (hits == null) return 0;
            foreach (ScanHit hit in hits)
            {
                if (hit == null || string.IsNullOrEmpty(hit.Exe)) continue;
                string error;
                if (AddGameExecutableCore(hit.Name, hit.Exe, hit.Root, false, out error)) added++;
                else if (!string.IsNullOrEmpty(error)) lastError = error;
            }
            if (added > 0)
            {
                lock (sync)
                    if (!PersistLibraryLocked()) return 0;
                KickLibraryChanged();
            }
            return added;
        }

        // 只由交接确认后的提交点调用 现场 PID/创建时间与 epoch 由调用方复核
        // 学习与 Boost 成功与否无关 候选 SafetyOnly 和强制入口都不能改写游戏库
        internal bool TryLearnConfirmedRenderer(GameDetection hit)
        {
            return TryLearnConfirmedRenderer(hit, null);
        }

        private bool TryLearnConfirmedRenderer(GameDetection hit, Func<bool> stillCurrent)
        {
            if (hit == null || hit.Profile == null || !hit.RendererCandidateSelected
                || !hit.RendererLearnable || hit.RendererSafetyOnly || hit.RequiresGpuConfirm
                || hit.Profile.ForceTrigger || hit.RendererPid <= 0 || hit.RendererCreation <= 0)
                return false;
            return TryLearnRendererCore(hit.Profile.Id, hit.RendererPath,
                hit.RendererName, hit.Profile, stillCurrent);
        }

        private bool TryLearnRendererCore(string profileId, string rendererPath,
            string rendererName, GameProfile observedProfile, Func<bool> stillCurrent)
        {
            if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(rendererPath)
                || string.IsNullOrWhiteSpace(rendererName))
                return false;
            string suppliedPath = rendererPath.Trim().Trim('"');
            string resolved = GameProfileStore.NormalizePath(suppliedPath);
            if (resolved == null || !Path.IsPathRooted(suppliedPath)) return false;
            string volume = Path.GetPathRoot(suppliedPath);
            // 盘符相对与根相对路径依赖当前工作目录 不是完整的进程身份
            if (string.IsNullOrEmpty(volume) || volume.Length < 3
                || (!volume.EndsWith("\\", StringComparison.Ordinal)
                    && !volume.EndsWith("/", StringComparison.Ordinal))) return false;
            string name = Path.GetFileNameWithoutExtension(resolved);
            if (string.IsNullOrEmpty(name)
                || !string.Equals(name, StripExe(rendererName), StringComparison.OrdinalIgnoreCase)
                || !GameSessionDetector.IsLibraryCandidate(name, resolved, windowsPrefix)) return false;

            string root = ResolveLibraryInstallRoot(resolved, GameScan.InferGameRoot(resolved));
            if (root != null && !FamilyBoundary.UnderRoot(resolved, root)) root = null;
            string learnedGame;
            lock (sync)
            {
                if (stopping || ProfileStoreSaveFailed || (stillCurrent != null && !stillCurrent())) return false;
                GameProfile current = FindProfileLocked(profileId);
                if (current == null || current.ForceTrigger
                    || RendererPathClaimedLocked(current, resolved)) return false;

                // 入口替换不应把已声明的游戏目录缩成 Menu/某个子渲染器目录
                // 否则随后启动的兄弟 EXE 会丢失关联 仅保留合法且确实包含目标的
                // 原范围 目标在原范围之外时 仍用上面保守推断的新 Root
                string declaredRoot = GameInstallScope.RestrictFallback(
                    current.ExecutablePath, NormalizeGameRoot(current.Root));
                if (declaredRoot != null && FamilyBoundary.UnderRoot(resolved, declaredRoot)) root = declaredRoot;

                bool alreadyTarget = string.Equals(current.ExecutablePath, resolved,
                    StringComparison.OrdinalIgnoreCase);
                // UI 可在确认等待中删除 重建或修改档案 旧观察不得覆盖新入口
                // 同一路径的重复确认允许幂等返回 不需要再次落盘
                if (!alreadyTarget && observedProfile != null
                    && (!string.Equals(current.ExecutablePath, observedProfile.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(current.LearnedExecutablePath, observedProfile.LearnedExecutablePath, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(current.Root, observedProfile.Root, StringComparison.OrdinalIgnoreCase)))
                    return false;
                if (alreadyTarget && current.LearnedExecutablePath == null
                    && string.Equals(current.Root, root, StringComparison.OrdinalIgnoreCase)
                    && current.Entries.Count == 1 && current.Entries.Contains(name)) return true;

                // 直接替换目标 不留旧入口或 Learned 别名 ID 用户名称与配置原样保留
                // 先保存独立候选 成功后才发布到内存 失败时原档案从未被改动
                GameProfile replacement = current.Clone();
                replacement.ExecutablePath = resolved;
                replacement.LearnedExecutablePath = null;
                replacement.Root = root;
                replacement.Entries.Clear();
                replacement.Entries.Add(name);
                int index = profiles.IndexOf(current);
                var next = new List<GameProfile>(profiles);
                next[index] = replacement;
                // 路径推断与磁盘准备之后 再在与生命周期失效共用的锁内终验
                if (stillCurrent != null && !stillCurrent()) return false;
                // 与 SaveProfilesLocked 使用相同的首错熔断 不重试 不绕过严格提交
                if (!profileStore.Save(next))
                {
                    SignalProfileStoreSaveFailure();
                    return false;
                }
                profiles[index] = replacement;
                if (!alreadyTarget) ForgetRendererObservation(profileId);
                learnedGame = replacement.Name;
            }
            Logger.Log(Lang.T("log.gamemodelibrary.2") + learnedGame + Lang.T("log.gamemodelibrary.3") + name
                + Lang.T("log.gamemodelibrary.4") + resolved + " ");
            RequestFullGameDetection();
            RaiseLibraryChanged();
            return true;
        }

        private bool RendererPathClaimedLocked(GameProfile self, string resolved)
        {
            foreach (GameProfile other in profiles)
            {
                if (ReferenceEquals(other, self)) continue;
                if (string.Equals(other.ExecutablePath, resolved, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(other.LearnedExecutablePath, resolved, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

#if PAVISE_SELFTEST
        internal void ProbeLearnRenderer(string profileId, string rendererPath, string rendererName)
        {
            TryLearnRendererCore(profileId, rendererPath, rendererName, null, null);
        }
#endif

        public bool SetProfileForceTrigger(string profileId, bool on)
        {
            bool changed = false, dropSession = false;
            string name = null;
            lock (sync)
            {
                if (stopping) return false;
                foreach (GameProfile p in profiles)
                {
                    if (!string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (on && string.IsNullOrEmpty(p.ExecutablePath)
                        && string.IsNullOrEmpty(p.LearnedExecutablePath)) return false;
                    if (p.ForceTrigger != on)
                    {
                        p.ForceTrigger = on;
                        name = p.Name;
                        changed = true;
                        if (!PersistLibraryLocked()) return false;
                    }
                    break;
                }
                if (changed && !on)
                    dropSession = activeDetection != null && activeDetection.Profile != null
                        && string.Equals(activeDetection.Profile.Id, profileId,
                            StringComparison.OrdinalIgnoreCase);
            }
            if (!changed) return true;
            if (dropSession) panicReq = true;
            Logger.Log((on ? Lang.T("log.gamemodelibrary.5") : Lang.T("log.gamemodelibrary.6")) + name
                + (on ? Lang.T("log.gamemodelibrary.7")
                     : Lang.T("log.gamemodelibrary.8")));
            KickLibraryChanged();
            return true;
        }

        private GameProfile FindProfileLocked(string profileId)
        {
            foreach (GameProfile p in profiles)
                if (string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        // 调用方须持有 sync 实时解析会话档案的布尔策略 无会话档案时用全局值
        //   档案已丢失按关处理 档案存在覆盖时以覆盖为准 三个实时解析的会话
        //   策略 待机清理/英文输入/Intel 低延迟 共用这一份规则
        private bool LiveBoolPreferenceLocked(string policyKey, bool globalOn)
        {
            PolicySnapshot snapshot = sessionPolicy;
            if (snapshot == null || string.IsNullOrEmpty(snapshot.ProfileId)) return globalOn;
            GameProfile profile = FindProfileLocked(snapshot.ProfileId);
            if (profile == null) return false;
            string value;
            return profile.Overrides.TryGetValue(policyKey, out value) ? value == "1" : globalOn;
        }

        // 会话策略键的失效钩子 set 与 clear 及批量清除共用这一份 新增实时
        //   解析的策略键在这里登记一次即可 漏接的表现是静默应用过期偏好
        //   调用方须持有 sync
        private void InvalidateOverrideWorkLocked(string key)
        {
            if (key == PolicyCatalog.KeyDisableCpuIdle || key == PolicyCatalog.KeyPowerPlan)
                System.Threading.Interlocked.Increment(ref cpuIdleGeneration);
            if (key == PolicyCatalog.KeyStandbyCleaner) InvalidateStandbyCleanerWork();
            if (key == PolicyCatalog.KeyEnglishInput) InvalidateEnglishInputWork();
            if (key == PolicyCatalog.KeyIntelLowLatency) InvalidateIntelGraphicsWork();
        }

        // 覆盖写入成功后的变更通知 effectiveOn 是该键此刻生效的布尔值
        //   设置时来自新覆盖值 清除时回落到全局开关
        private void NotifyOverridePolicyChanged(string key, bool effectiveOn)
        {
            if (key == PolicyCatalog.KeyPauseServices) PauseServicesPolicyChanged(effectiveOn);
            else if (key == PolicyCatalog.KeyDisableCpuIdle) CpuIdlePolicyChanged(effectiveOn);
            else if (key == PolicyCatalog.KeyStandbyCleaner) StandbyCleanerPolicyChanged(effectiveOn);
            else if (key == PolicyCatalog.KeyIntelLowLatency) IntelGraphicsPolicyChanged(effectiveOn);
            else if (key == PolicyCatalog.KeyPowerPlan || key == PolicyCatalog.KeyEnglishInput)
                RequestPolicyApply();
        }

        private bool GlobalPolicyOn(string key)
        {
            if (key == PolicyCatalog.KeyPauseServices) return pauseServicesOn;
            if (key == PolicyCatalog.KeyDisableCpuIdle) return disableCpuIdleOn;
            if (key == PolicyCatalog.KeyStandbyCleaner) return standbyCleanerOn;
            if (key == PolicyCatalog.KeyIntelLowLatency) return intelLowLatencyOn;
            return false;
        }

        public bool SetProfileOverride(string profileId, string key, string value)
        {
            if (key == PolicyCatalog.KeySuppressFamily)
            {
                string canonical = PolicyCatalog.Canonical(key, value);
                return canonical != null && SetProfileFamilySuppression(profileId, canonical == "1");
            }
            bool ok = false;
            lock (sync)
            {
                if (stopping) return false;
                if (key == PolicyCatalog.KeyStandbyCleaner
                    && PolicyCatalog.Canonical(key, value) == "1" && !standbyCleanerOptionsValid) return false;
                GameProfile p = FindProfileLocked(profileId);
                if (p != null)
                {
                    InvalidateOverrideWorkLocked(key);
                    ok = PolicyResolver.SetOverride(p, key, value);
                    if (ok && !SaveProfilesLocked()) ok = false;
                }
            }
            if (ok) NotifyOverridePolicyChanged(key, PolicyCatalog.Canonical(key, value) == "1");
            return ok;
        }

        public bool ClearProfileOverride(string profileId, string key)
        {
            if (key == PolicyCatalog.KeySuppressFamily) return SetProfileFamilySuppression(profileId, false);
            bool ok = false;
            lock (sync)
            {
                if (stopping) return false;
                GameProfile p = FindProfileLocked(profileId);
                if (p != null && p.Overrides.ContainsKey(key))
                {
                    InvalidateOverrideWorkLocked(key);
                    p.Overrides.Remove(key);
                    ok = SaveProfilesLocked();
                }
            }
            if (ok) NotifyOverridePolicyChanged(key, GlobalPolicyOn(key));
            return ok;
        }

        public int ClearProfileOverrides(string profileId)
        {
            int n = 0;
            string name = null;
            bool servicesChanged = false;
            bool cpuIdleChanged = false;
            bool standbyCleanerChanged = false;
            bool intelChanged = false;
            lock (familyPolicyGate)
            {
                lock (sync)
                {
                    if (stopping) return 0;
                    GameProfile p = FindProfileLocked(profileId);
                    if (p != null && p.Overrides.Count > 0 && !ProfileStoreSaveFailed)
                    {
                        cpuIdleChanged = p.Overrides.ContainsKey(PolicyCatalog.KeyDisableCpuIdle)
                            || p.Overrides.ContainsKey(PolicyCatalog.KeyPowerPlan);
                        standbyCleanerChanged = p.Overrides.ContainsKey(PolicyCatalog.KeyStandbyCleaner);
                        intelChanged = p.Overrides.ContainsKey(PolicyCatalog.KeyIntelLowLatency);
                        foreach (string overrideKey in p.Overrides.Keys)
                            InvalidateOverrideWorkLocked(overrideKey);
                        GameProfile replacement = p.Clone();
                        n = PolicyResolver.ClearAllOverrides(replacement);
                        var next = new List<GameProfile>(profiles);
                        int index = profiles.IndexOf(p);
                        next[index] = replacement;
                        if (!profileStore.Save(next))
                        {
                            SignalProfileStoreSaveFailure();
                            return 0;
                        }
                        profiles[index] = replacement;
                        servicesChanged = p.Overrides.ContainsKey(PolicyCatalog.KeyPauseServices);
                        name = p.Name;
                        InvalidateFamilyPolicy();
                    }
                }
            }
            if (n > 0)
            {
                if (servicesChanged) PauseServicesPolicyChanged(pauseServicesOn);
                if (cpuIdleChanged) CpuIdlePolicyChanged(disableCpuIdleOn);
                if (standbyCleanerChanged) StandbyCleanerPolicyChanged(standbyCleanerOn);
                if (intelChanged) IntelGraphicsPolicyChanged(intelLowLatencyOn);
                if (!servicesChanged && !cpuIdleChanged && !standbyCleanerChanged && !intelChanged) RequestPolicyApply();
                RaiseLibraryChanged();
            }
            if (name != null) Logger.Log(Lang.T("log.gamemodelibrary.9") + name + Lang.T("log.gamemodelibrary.10") + n + Lang.T("log.gamemodelibrary.11"));
            return n;
        }

        public List<PolicyDiff> DiffProfile(string profileId)
        {
            GameProfile copy = null;
            lock (sync)
            {
                GameProfile p = FindProfileLocked(profileId);
                if (p != null) copy = p.Clone();
            }
            return PolicyResolver.Diff(copy);
        }

        public bool RenameProfile(string profileId, string name)
        {
            string trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0) return false;
            string oldName = null;
            lock (sync)
            {
                if (stopping) return false;
                GameProfile p = FindProfileLocked(profileId);
                if (p == null) return false;
                if (string.Equals(p.Name, trimmed, StringComparison.Ordinal)) return true;
                oldName = p.Name;
                p.Name = trimmed;
                if (!SaveProfilesLocked()) return false;
            }
            Logger.Log(Lang.T("log.gamemodelibrary.14") + oldName + Lang.T("log.gamemodelibrary.15") + trimmed + Lang.T("log.gamemodelibrary.16"));
            RaiseLibraryChanged();
            return true;
        }

        public void RemoveProfile(string profileId)
        {
            bool dropSession;
            lock (sync)
            {
                if (stopping) return;
                bool ignoreDirty = false;
                foreach (GameProfile p in profiles)
                {
                    if (!string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(p.ExecutablePath) && autoAddIgnore.Add(p.ExecutablePath))
                        ignoreDirty = true;
                    if (!string.IsNullOrEmpty(p.LearnedExecutablePath) && autoAddIgnore.Add(p.LearnedExecutablePath))
                        ignoreDirty = true;
                }
                if (ignoreDirty && !SaveAutoIgnoreLocked()) return;
                InvalidateStandbyCleanerWork();
                InvalidateEnglishInputWork();
                InvalidateIntelGraphicsWork();
                profiles.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
                if (!PersistLibraryLocked()) return;
                ClearFamilyDiscovery();
                ForgetRendererObservation(profileId);
                dropSession = activeDetection != null && activeDetection.Profile != null
                    && string.Equals(activeDetection.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase);
                if (dropSession) panicReq = true;
                InvalidateRendererHandoff();
            }
            RequestFullGameDetection();
            RequestPolicyApply();
            RaiseLibraryChanged();
        }

#if PAVISE_SELFTEST
        public List<string> GetWhitelist()
        {
            var result = new List<string>();
            lock (sync)
                foreach (WhitelistRule rule in whiteRules) result.Add(rule.Value);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
#endif

        public List<WhitelistRuleView> GetWhitelistRules()
        {
            List<WhitelistRuleView> result = SnapshotWhitelistRuleViews();
            result.Sort(delegate(WhitelistRuleView a, WhitelistRuleView b)
            {
                int kind = a.Rule.Kind.CompareTo(b.Rule.Kind);
                return kind != 0 ? kind : string.Compare(a.Rule.Value, b.Rule.Value, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        public List<WhitelistRuleView> GetWhitelistRulesFast()
        {
            var result = new List<WhitelistRuleView>();
            lock (sync)
                foreach (WhitelistRule rule in whiteRules)
                    result.Add(new WhitelistRuleView(
                        rule, -1, rule.Kind == WhitelistRuleKind.LegacyName
                            && IsPresetWhitelistName(rule.Value)));
            result.Sort(delegate(WhitelistRuleView a, WhitelistRuleView b)
            {
                int kind = a.Rule.Kind.CompareTo(b.Rule.Kind);
                return kind != 0 ? kind : string.Compare(
                    a.Rule.Value, b.Rule.Value, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        public bool AddWhitelistPath(string executablePath)
        {
            return AddWhitelistRule(WhitelistRuleKind.ExactPath, executablePath);
        }

        public bool AddWhitelistAuto(string executablePath)
        {
            return AddWhitelistRule(ResolveAutoKind(executablePath), executablePath);
        }

        internal static WhitelistRuleKind ResolveAutoKind(string executablePath)
        {
            return WhitelistRule.IsUnsafeFamilyAnchor(executablePath)
                ? WhitelistRuleKind.ExactPath
                : WhitelistRuleKind.ApplicationFamily;
        }

        public bool NarrowWhitelistRule(string key)
        {
            WhitelistRule found = null;
            lock (sync)
                foreach (WhitelistRule rule in whiteRules)
                    if (rule.Key == key) { found = rule; break; }
            if (found == null || found.Kind != WhitelistRuleKind.ApplicationFamily) return false;
            if (!RemoveWhitelistRule(key)) return false;
            if (AddWhitelistRule(WhitelistRuleKind.ExactPath, found.Value)) return true;
            AddWhitelistRule(WhitelistRuleKind.ApplicationFamily, found.Value);
            return false;
        }

        public bool WidenWhitelistRule(string key)
        {
            WhitelistRule found = null;
            lock (sync)
                foreach (WhitelistRule rule in whiteRules)
                    if (rule.Key == key) { found = rule; break; }
            if (found == null || found.Kind != WhitelistRuleKind.ExactPath) return false;
            if (WhitelistRule.IsUnsafeFamilyAnchor(found.Value)) return false;
            if (!RemoveWhitelistRule(key)) return false;
            if (AddWhitelistRule(WhitelistRuleKind.ApplicationFamily, found.Value)) return true;
            AddWhitelistRule(WhitelistRuleKind.ExactPath, found.Value);
            return false;
        }

        private bool AddWhitelistRule(WhitelistRuleKind kind, string value)
        {
            WhitelistRule rule;
            if (!WhitelistRule.TryCreate(kind, value, out rule))
            {
                lock (sync) whitelistLastError = Lang.T("white.duplicate");
                return false;
            }
            int matched, freed;
            lock (whiteEvalSync)
            {
                lock (sync)
                {
                    if (stopping) return false;
                    if (whiteRuleKeys.Contains(rule.Key))
                    {
                        whitelistLastError = Lang.T("white.duplicate");
                        return false;
                    }
                    var next = new List<WhitelistRule>(whiteRules) { rule };
                    if (!SaveWhite(next))
                    {
                        whitelistLastError = Lang.T("white.save.failed");
                        return false;
                    }
                    AddWhiteRuleNoSave(rule);
                    whitelistLastError = "";
                }
                // 把还原工作一起算进 Stop 的白名单排干里 不能只算文件提交
                // 任何原生释放都不许拖在一次成功的停止后面
                freed = ReleaseCurrentWhitelistMatches(out matched);
            }
            Logger.Log(Lang.T("log.gamemodelibrary.17") + rule.Kind + " " + rule.Value + Lang.T("log.gamemodelibrary.18") + matched
                + Lang.T("log.gamemodelibrary.19") + freed + Lang.T("log.gamemodelibrary.20"));
            RequestPolicyApply();
            return true;
        }

        public bool RemoveWhitelistRule(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (whiteEvalSync)
            {
                lock (sync)
                {
                    if (stopping) return false;
                    WhitelistRule target = whiteRules.Find(delegate(WhitelistRule rule)
                    {
                        return string.Equals(rule.Key, key, StringComparison.OrdinalIgnoreCase);
                    });
                    if (target == null) return false;
                    if (target.Kind == WhitelistRuleKind.LegacyName
                        && IsPresetWhitelistName(target.Value))
                    {
                        whitelistLastError = Lang.T("white.required");
                        return false;
                    }
                    var next = new List<WhitelistRule>(whiteRules);
                    next.Remove(target);
                    if (!SaveWhite(next))
                    {
                        whitelistLastError = Lang.T("white.save.failed");
                        return false;
                    }
                    whiteRules.Remove(target);
                    whiteRuleKeys.Remove(key);
                    whiteFamilyMembers.Remove(key);
                    whiteRevision++;
                    RefreshWhitelistFamilyFlagLocked();
                    whitelistLastError = "";
                }
            }
            RequestPolicyApply();
            return true;
        }

        public bool ResetWhitelist()
        {
            var next = new List<WhitelistRule>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in SystemProcessCatalog.PresetWhitelist)
            {
                WhitelistRule rule;
                if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, entry, out rule)
                    && keys.Add(rule.Key)) next.Add(rule);
            }
            int matched, freed;
            lock (whiteEvalSync)
            {
                lock (sync)
                {
                    if (stopping) return false;
                    if (!SaveWhite(next))
                    {
                        whitelistLastError = Lang.T("white.save.failed");
                        return false;
                    }
                    whiteRules.Clear();
                    whiteRuleKeys.Clear();
                    whiteFamilyMembers.Clear();
                    whiteRevision++;
                    RefreshWhitelistFamilyFlagLocked();
                    foreach (WhitelistRule rule in next)
                        AddWhiteRuleNoSave(rule);
                    whitelistLastError = "";
                }
                freed = ReleaseCurrentWhitelistMatches(out matched);
            }
            Logger.Log(Lang.T("log.gamemodelibrary.21") + SystemProcessCatalog.PresetWhitelist.Length + Lang.T("log.gamemodelibrary.22") + matched
                + Lang.T("log.gamemodelibrary.19") + freed + Lang.T("log.gamemodelibrary.20"));
            RequestPolicyApply();
            return true;
        }

        private bool SaveWhite(IList<WhitelistRule> rules)
        {
            lock (sync)
            {
                if (stopping) return false;
                try
                {
                    var lines = new List<string>();
                    lines.Add(Lang.T("t.gamemodelibrary.23"));
                    lines.Add(Lang.T("t.gamemodelibrary.24"));
                    lines.Add(Lang.T("t.gamemodelibrary.25"));
                    lines.Add(WhitelistRule.Header);
                    if (rules != null)
                        foreach (WhitelistRule rule in rules) lines.Add(rule.Serialize());
                    lines.Add(BuildWhitelistFooter(rules));
                    return AtomicFile.WriteLines(whitePath, lines.ToArray(), Lang.T("nav.white"));
                }
                catch (Exception error)
                {
                    Logger.LogFailure(Lang.T("log.gamemodelibrary.26"), error);
                    return false;
                }
            }
        }

        internal static string BuildWhitelistFooter(IList<WhitelistRule> rules)
        {
            ulong hash = 1469598103934665603UL;
            int count = 0;
            if (rules != null)
                foreach (WhitelistRule rule in rules)
                {
                    if (rule == null) continue;
                    string line = rule.Serialize();
                    count++;
                    unchecked
                    {
                        for (int i = 0; i < line.Length; i++)
                        {
                            hash ^= (byte)line[i];
                            hash *= 1099511628211UL;
                        }
                        hash ^= (byte)'\n';
                        hash *= 1099511628211UL;
                    }
                }
            return WhitelistFooterPrefix + count + "|" + hash.ToString("X16");
        }

        private static string DisplayName(string executablePath, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(fallback)) return fallback.Trim();
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(executablePath);
                string value = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            catch { }
            return Path.GetFileNameWithoutExtension(executablePath);
        }

        private static string NormalizeGameRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            try
            {
                string full = Path.GetFullPath(root.Trim().Trim('"')).TrimEnd('\\');
                return FamilyBoundary.SafeFamilyDir(full) ? full : null;
            }
            catch { return null; }
        }
    }
}
