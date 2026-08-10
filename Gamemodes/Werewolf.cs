using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using Hazel;
using UnityEngine;

namespace EHR.Gamemodes;

internal static class Werewolf
{
    private enum Phase { Preparing, Night, Seer, WolfMeeting, WolfAction, WitchAction, HunterAction, MagicianMeeting, Searching, DayMeeting }
    private enum WwRole { Wolf, RavenWolf, Villager, Seer, Witch, DemonHunter, Magician }

    private const CustomGameMode GameMode = CustomGameMode.Werewolf;
    private static readonly Color ModeColor = new Color32(111, 34, 139, byte.MaxValue);
    private static readonly Dictionary<byte, WwRole> Roles = [];
    private static readonly Dictionary<byte, Vector2> NightStartPositions = [];
    private static readonly HashSet<byte> PreparedPlayers = [];
    private static readonly HashSet<byte> DeadTonight = [];
    private static byte WolfTarget = byte.MaxValue;
    private static byte WitchSaveTarget = byte.MaxValue;
    private static int NightNumber;
    private static Phase CurrentPhase;
    private static long PhaseEndTS;

    private static OptionItem PrepareTime;
    private static OptionItem WolfMeetingTime;
    private static OptionItem WolfActionTime;
    private static OptionItem WolfVision;
    private static OptionItem WolfSpeed;
    private static OptionItem WitchActionTime;
    private static OptionItem WitchVision;
    private static OptionItem WitchSpeed;
    private static OptionItem WitchAntidotes;
    private static OptionItem WitchPoisons;
    private static OptionItem HunterActionTime;
    private static OptionItem HunterVision;
    private static OptionItem HunterSpeed;
    private static OptionItem MagicianActionTime;
    private static OptionItem SearchTime;
    private static OptionItem SearchTimeIncrease;
    private static OptionItem DayMeetingTime;

    public static bool IsEnable => Options.CurrentGameMode == GameMode;

    public static void SetupCustomOption()
    {
        PrepareTime = Seconds(69_230_001, "Werewolf_PrepareTime", 30, true);
        WolfMeetingTime = Seconds(69_230_002, "Werewolf_WolfMeetingTime", 30);
        WolfActionTime = Seconds(69_230_003, "Werewolf_WolfActionTime", 20);
        WolfVision = Multiplier(69_230_004, "Werewolf_WolfVision", 1f);
        WolfSpeed = Multiplier(69_230_005, "Werewolf_WolfSpeed", 1.25f);
        WitchActionTime = Seconds(69_230_006, "Werewolf_WitchActionTime", 20, true);
        WitchVision = Multiplier(69_230_007, "Werewolf_WitchVision", 1f);
        WitchSpeed = Multiplier(69_230_008, "Werewolf_WitchSpeed", 1.25f);
        WitchAntidotes = Count(69_230_009, "Werewolf_WitchAntidotes", 1);
        WitchPoisons = Count(69_230_010, "Werewolf_WitchPoisons", 1);
        HunterActionTime = Seconds(69_230_011, "Werewolf_HunterActionTime", 20, true);
        HunterVision = Multiplier(69_230_012, "Werewolf_HunterVision", 1f);
        HunterSpeed = Multiplier(69_230_013, "Werewolf_HunterSpeed", 1.25f);
        MagicianActionTime = Seconds(69_230_014, "Werewolf_MagicianActionTime", 15, true);
        SearchTime = Seconds(69_230_015, "Werewolf_SearchTime", 20, true);
        SearchTimeIncrease = Seconds(69_230_016, "Werewolf_SearchTimeIncrease", 5);
        DayMeetingTime = Seconds(69_230_017, "Werewolf_DayMeetingTime", 60, true);
    }

    private static OptionItem Seconds(int id, string name, int value, bool header = false) => new IntegerOptionItem(id, name, new(0, 180, 5), value, TabGroup.GameSettings).SetHeader(header).SetGameMode(GameMode).SetValueFormat(OptionFormat.Seconds).SetColor(ModeColor);
    private static OptionItem Count(int id, string name, int value) => new IntegerOptionItem(id, name, new(0, 5, 1), value, TabGroup.GameSettings).SetGameMode(GameMode).SetValueFormat(OptionFormat.Times).SetColor(ModeColor);
    private static OptionItem Multiplier(int id, string name, float value) => new FloatOptionItem(id, name, new(0f, 5f, 0.25f), value, TabGroup.GameSettings).SetGameMode(GameMode).SetValueFormat(OptionFormat.Multiplier).SetColor(ModeColor);

    public static void Init()
    {
        Roles.Clear();
        NightStartPositions.Clear();
        PreparedPlayers.Clear();
        DeadTonight.Clear();
        WolfTarget = WitchSaveTarget = byte.MaxValue;
        NightNumber = 0;
        CurrentPhase = Phase.Preparing;
        PhaseEndTS = Utils.TimeStamp + PrepareTime.GetInt();
    }

    public static void OnGameStart()
    {
        AssignRoles();
        foreach ((byte id, WwRole role) in Roles)
        {
            PlayerControl pc = Utils.GetPlayerById(id);
            pc?.RpcChangeRoleBasis(role switch
            {
                WwRole.Wolf or WwRole.RavenWolf or WwRole.DemonHunter => CustomRoles.Impostor,
                WwRole.Witch => CustomRoles.Shapeshifter,
                _ => CustomRoles.Crewmate
            });
            pc?.Notify(GetRoleInfo(role), 30f, true, false);
        }
        Utils.SendMessage(Translator.GetString("Werewolf.ReadyBroadcast"));
    }

    private static void AssignRoles()
    {
        List<PlayerControl> players = Main.EnumeratePlayerControls().Where(x => x.PlayerId < 254).OrderBy(_ => IRandom.Instance.Next(10000)).ToList();
        if (players.Count == 0) return;
        void Set(int index, WwRole role) { if (index < players.Count) Roles[players[index].PlayerId] = role; }
        Set(0, WwRole.RavenWolf); Set(1, WwRole.Wolf); Set(2, WwRole.Seer); Set(3, WwRole.Witch); Set(4, WwRole.DemonHunter); Set(5, WwRole.Magician);
        foreach (PlayerControl pc in players) Roles.TryAdd(pc.PlayerId, WwRole.Villager);
    }

    public static string GetSuffix(PlayerControl seer, PlayerControl target)
    {
        if (!Roles.TryGetValue(seer.PlayerId, out WwRole role)) return string.Empty;
        if (seer.PlayerId == target.PlayerId) return $"<size=80%>{GetRoleInfo(role)}</size>";
        if (IsWolf(role) && Roles.TryGetValue(target.PlayerId, out WwRole tr) && IsWolf(tr)) return $"<size=80%>{Translator.GetString("Werewolf.WolfAlly")}</size>";
        return string.Empty;
    }

    public static bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (!Roles.TryGetValue(killer.PlayerId, out WwRole role)) return false;
        if (CurrentPhase == Phase.WolfAction && IsWolf(role) && target.PlayerId == WolfTarget) { killer.Kill(target); DeadTonight.Add(target.PlayerId); return false; }
        if (CurrentPhase == Phase.HunterAction && role == WwRole.DemonHunter)
        {
            if (Roles.TryGetValue(target.PlayerId, out WwRole tr) && !IsGood(tr)) { killer.Kill(target); DeadTonight.Add(target.PlayerId); }
            else { killer.Notify(Translator.GetString("Werewolf.HunterBackfire")); killer.Kill(killer); }
            return false;
        }
        killer.Notify(Translator.GetString("Werewolf.InvalidKill"));
        return false;
    }

    public static bool HandleCommand(PlayerControl pc, string text, string[] args)
    {
        if (!IsEnable || !Roles.TryGetValue(pc.PlayerId, out WwRole role)) return false;
        if (text.StartsWith("/secondkill", StringComparison.OrdinalIgnoreCase) && role == WwRole.RavenWolf && args.Length > 1 && byte.TryParse(args[1], out byte sid)) { Utils.GetPlayerById(sid)?.RpcMurderPlayer(Utils.GetPlayerById(sid)); DeadTonight.Add(sid); pc.Notify(Translator.GetString("Werewolf.SecondKillUsed")); return true; }
        if (text.StartsWith("/changeplace", StringComparison.OrdinalIgnoreCase) && role == WwRole.Magician && args.Length > 2 && byte.TryParse(args[1], out byte a) && byte.TryParse(args[2], out byte b)) { Swap(a, b); pc.Notify(Translator.GetString("Werewolf.ChangePlaceDone")); return true; }
        return false;
    }

    private static void Swap(byte a, byte b)
    {
        PlayerControl pa = Utils.GetPlayerById(a), pb = Utils.GetPlayerById(b);
        if (pa == null || pb == null) return;
        Vector2 pos = pa.Pos();
        pa.TP(pb.Pos()); pb.TP(pos);
    }

    private static string GetRoleInfo(WwRole role) => Translator.GetString($"Werewolf.RoleInfo.{role}");
    private static bool IsWolf(WwRole role) => role is WwRole.Wolf or WwRole.RavenWolf;
    private static bool IsGood(WwRole role) => role is WwRole.Villager or WwRole.Seer or WwRole.Witch or WwRole.DemonHunter;
}
