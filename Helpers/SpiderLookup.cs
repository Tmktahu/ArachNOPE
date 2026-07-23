using System.Collections.Generic;
using Stunlock.Core;

namespace ArachNOPE.Helpers;

internal static class SpiderLookup
{
    // Furniture GUIDs
    const int TM_Laboratory_Chair_Leather_01 = -813681645;
    const int TM_Strongblade_Chair03 = -315172854;
    const int TM_Elris_Stool_02 = 188374087;
    const int TM_Elris_Stool_01 = -1041511902;
    const int TM_Castle_Module_Child_RoundTable_3x3_StrongbladeDLC02 = -70745613;
    const int TM_BanditLeaderChair_01 = -154796222;
    const int TM_Elris_Bags_01 = 167350142;
    const int TM_Castle_Throne_01DLC02Variant = -625213071;
    const int TM_Castle_Throne_01_StrongbladeDLC = 980816206;
    const int TM_GloomRot_Laboratory_Chair_Body01 = 114706283;
    const int TM_Castle_Module_Child_RoundTable_6x6_StrongbladeDLC03 = 1592427758;
    const int TM_Castle_Module_Child_RoundTable_3x3_StrongbladeDLC03 = -784399817;
    const int TM_Castle_ObjectDecor_ChessTable01 = 624218349;
    const int TM_Castle_ObjectDecor_Chair_StrongbladeDLC01 = 930898512;

    // Spider GUIDs
    const int CHAR_Spider_Melee = 2136899683;
    const int CHAR_Spider_Melee_Summon = 2119230788;
    const int CHAR_Spider_Melee_GateBoss_Summon = -725251219;
    const int CHAR_Spider_Range = 2103131615;
    const int CHAR_Spider_Range_Summon = 1974733695;
    const int CHAR_Spider_Spiderling_VerminNest = 1767714956;
    const int CHAR_Spiderling_Summon = -18289884;
    const int CHAR_Spider_Spiderling = 1078424589;
    const int CHAR_Spider_Baneling = -764515001;
    const int CHAR_Spider_Baneling_Summon = -1004061470;
    const int CHAR_Spider_Forest = -581295882;
    const int CHAR_Spider_Forestling = 574276383;
    const int CHAR_Spider_Broodmother = 342127250;
    const int CHAR_Spider_Queen_VBlood = -548489519;
    const int CHAR_Spider_Queen_VBlood_GateBoss_Major = -943858353;
    const int CHAR_Unholy_UnstableArachnid = 834333879;
    const int CHAR_Unholy_UnstableArachnid_Small = -1106602776;

    static readonly HashSet<int> SpiderGuidHashes = new()
    {
        CHAR_Spider_Baneling,
        CHAR_Spider_Baneling_Summon,
        CHAR_Spider_Broodmother,
        CHAR_Spider_Forest,
        CHAR_Spider_Forestling,
        CHAR_Spider_Melee,
        CHAR_Spider_Melee_GateBoss_Summon,
        CHAR_Spider_Melee_Summon,
        CHAR_Spider_Queen_VBlood,
        CHAR_Spider_Queen_VBlood_GateBoss_Major,
        CHAR_Spider_Range,
        CHAR_Spider_Range_Summon,
        CHAR_Spider_Spiderling,
        CHAR_Spider_Spiderling_VerminNest,
        CHAR_Spiderling_Summon,
        CHAR_Unholy_UnstableArachnid,
        CHAR_Unholy_UnstableArachnid_Small
    };

    // Spider type → furniture GUID hash
    static readonly Dictionary<int, int> SpiderToFurniture = new()
    {
        // Melee spiders
        { CHAR_Spider_Melee, TM_Laboratory_Chair_Leather_01 },
        { CHAR_Spider_Melee_Summon, TM_Laboratory_Chair_Leather_01 },
        { CHAR_Spider_Melee_GateBoss_Summon, TM_Laboratory_Chair_Leather_01 },

        // Ranged spiders
        { CHAR_Spider_Range, TM_Strongblade_Chair03 },
        { CHAR_Spider_Range_Summon, TM_Strongblade_Chair03 },

        // Spiderlings
        { CHAR_Spider_Spiderling, TM_Castle_ObjectDecor_ChessTable01 },
        { CHAR_Spider_Spiderling_VerminNest, TM_Elris_Stool_02 },
        { CHAR_Spiderling_Summon, TM_Elris_Stool_01 },

        // Baneling
        { CHAR_Spider_Baneling, TM_Castle_Module_Child_RoundTable_3x3_StrongbladeDLC02 },
        { CHAR_Spider_Baneling_Summon, TM_Castle_Module_Child_RoundTable_3x3_StrongbladeDLC02 },

        // Forest spiders
        { CHAR_Spider_Forest, TM_BanditLeaderChair_01 },
        { CHAR_Spider_Forestling, TM_Elris_Bags_01 },

        // Boss spiders
        { CHAR_Spider_Queen_VBlood, TM_Castle_Throne_01DLC02Variant },
        { CHAR_Spider_Queen_VBlood_GateBoss_Major, TM_Castle_Throne_01_StrongbladeDLC },
        { CHAR_Spider_Broodmother, TM_GloomRot_Laboratory_Chair_Body01 },

        { CHAR_Unholy_UnstableArachnid, TM_Castle_Module_Child_RoundTable_6x6_StrongbladeDLC03 },
        { CHAR_Unholy_UnstableArachnid_Small, TM_Castle_Module_Child_RoundTable_3x3_StrongbladeDLC03 },

        // Player spider shapeshift buff → furniture
    };

    // Buff GUIDs
    internal const int AB_Shapeshift_Spider_Buff = 124832551;

    static readonly HashSet<int> SpiderBuffHashes = new()
    {
        AB_Shapeshift_Spider_Buff
    };

    static readonly Dictionary<int, int> BuffToFurniture = new()
    {
        { AB_Shapeshift_Spider_Buff, TM_Castle_ObjectDecor_Chair_StrongbladeDLC01 }
    };

    public static bool IsSpider(int guidHash) => SpiderGuidHashes.Contains(guidHash);

    public static bool IsSpiderBuff(int buffGuidHash) => SpiderBuffHashes.Contains(buffGuidHash);

    public static int GetFurnitureGuidHash(int spiderGuidHash)
    {
        return SpiderToFurniture.TryGetValue(spiderGuidHash, out var furniture) ? furniture : TM_Castle_ObjectDecor_ChessTable01;
    }

    public static int GetFurnitureGuidHashForBuff(int buffGuidHash)
    {
        return BuffToFurniture.TryGetValue(buffGuidHash, out var furniture) ? furniture : TM_Castle_ObjectDecor_ChessTable01;
    }
}
