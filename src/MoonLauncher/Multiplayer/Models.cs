using System;
using System.Collections.Generic;
using MoonLauncher.Core;

namespace MoonLauncher.Multiplayer
{
    /// <summary>Terracotta 官方 HTTP API 返回的一位玩家资料。</summary>
    public sealed class TerracottaProfile
    {
        public string MachineId { get; private set; }
        public string Name { get; private set; }
        public string Vendor { get; private set; }
        /// <summary>官方值：HOST、LOCAL 或 GUEST。</summary>
        public string Kind { get; private set; }

        internal static TerracottaProfile Parse(JNode node)
        {
            if (!node.IsObj) throw new FormatException("Terracotta profiles 中含有无效项目");
            var profile = new TerracottaProfile
            {
                MachineId = node["machine_id"].Str,
                Name = node["name"].Str,
                Vendor = node["vendor"].Str,
                Kind = node["kind"].Str,
            };
            if (profile.MachineId == null || profile.Name == null || profile.Vendor == null || profile.Kind == null)
                throw new FormatException("Terracotta profile 字段不完整");
            return profile;
        }
    }

    /// <summary>
    /// Terracotta /state 的不可变快照。只有官方 host-ok / guest-ok 会令 IsReady 为 true；
    /// ServerUrl 也只保留官方 guest-ok.url，不由启动器推导或伪造。
    /// </summary>
    public sealed class TerracottaState
    {
        public const string Waiting = "waiting";
        public const string HostScanning = "host-scanning";
        public const string HostStarting = "host-starting";
        public const string HostOk = "host-ok";
        public const string GuestConnecting = "guest-connecting";
        public const string GuestStarting = "guest-starting";
        public const string GuestOk = "guest-ok";
        public const string Exception = "exception";
        public const string Unavailable = "unavailable";

        static readonly IReadOnlyList<TerracottaProfile> NoProfiles = new TerracottaProfile[0];

        public long Index { get; private set; }
        public long ProfileIndex { get; private set; }
        public string State { get; private set; }
        public string Room { get; private set; }
        public string Difficulty { get; private set; }
        public string Url { get; private set; }
        /// <summary>官方异常类型 0..5；非 exception 状态为 null。</summary>
        public int? ExceptionType { get; private set; }
        public IReadOnlyList<TerracottaProfile> Profiles { get; private set; }
        /// <summary>false 仅表示本地进程/HTTP 服务不可用，不是 Terracotta 的官方房间状态。</summary>
        public bool IsAvailable { get; private set; }
        public string Error { get; private set; }

        public bool IsReady => IsAvailable && (State == HostOk || State == GuestOk);
        public bool IsGuestReady => IsAvailable && State == GuestOk;
        /// <summary>一键进服只能读取此属性；非 guest-ok 时始终为 null。</summary>
        public string ServerUrl => IsGuestReady ? Url : null;

        internal static TerracottaState Parse(JNode node)
        {
            if (!node.IsObj) throw new FormatException("Terracotta /state 返回值不是 JSON 对象");
            var state = new TerracottaState
            {
                Index = node["index"].Long,
                ProfileIndex = node["profile_index"].LongOr(0),
                State = node["state"].Str,
                Room = node["room"].Str,
                Difficulty = node["difficulty"].Str,
                Url = node["url"].Str,
                Profiles = NoProfiles,
                IsAvailable = true,
            };
            if (!node.Has("index") || state.Index < 0 || !IsOfficialState(state.State))
                throw new FormatException("Terracotta /state 缺少有效的 state/index");

            if (node.Has("type"))
            {
                int type = node["type"].Int;
                if (type < 0 || type > 5) throw new FormatException("Terracotta 异常类型超出 0..5");
                state.ExceptionType = type;
            }
            if (state.State == Exception && state.ExceptionType == null)
                throw new FormatException("Terracotta exception 状态缺少 type");

            if ((state.State == HostStarting || state.State == HostOk ||
                 state.State == GuestConnecting || state.State == GuestStarting) && string.IsNullOrWhiteSpace(state.Room))
                throw new FormatException("Terracotta 房间状态缺少 room");
            if (state.State == GuestOk && string.IsNullOrWhiteSpace(state.Url))
                throw new FormatException("Terracotta guest-ok 状态缺少 url");

            if (state.State == HostOk || state.State == GuestOk)
            {
                if (!node["profiles"].IsArr) throw new FormatException("Terracotta 就绪状态缺少 profiles");
                var profiles = new List<TerracottaProfile>();
                foreach (var item in node["profiles"]) profiles.Add(TerracottaProfile.Parse(item));
                state.Profiles = profiles.AsReadOnly();
            }
            return state;
        }

        internal static TerracottaState ProcessUnavailable(long index, string error)
        {
            return new TerracottaState
            {
                Index = index,
                State = Unavailable,
                Profiles = NoProfiles,
                IsAvailable = false,
                Error = error,
            };
        }

        static bool IsOfficialState(string state)
        {
            return state == Waiting || state == HostScanning || state == HostStarting || state == HostOk ||
                   state == GuestConnecting || state == GuestStarting || state == GuestOk || state == Exception;
        }

        public override string ToString() => IsAvailable ? $"{State} (#{Index})" : $"{State}: {Error}";
    }
}
