using System;
using System.Collections.Generic;

namespace PvfLib
{
    
    
    
    
    public class NpcFile : PvfModelBase
    {
        #region 基本信息

        
        public List<string> Names { get; set; } = new List<string>();
        
        public string Name => Names.Count > 0 ? Names[0] : null;
        public string FieldName { get; set; }
        
        public string Role { get; set; }

        #endregion

        #region 外观/动画

        public string FieldAnimation { get; set; }
        public int LookAround { get; set; } = -1;
        
        public string PopupFace { get; set; }
        public string SmallFace { get; set; }
        public string BigFace { get; set; }
        public string Img { get; set; }
        public string ClickArea { get; set; }

        #endregion

        #region 声音

        public string FieldWav { get; set; }
        public string PopupWav { get; set; }

        #endregion

        #region 对话

        
        public List<string> Dialogs { get; set; } = new List<string>();
        
        public string BalloonMessage { get; set; }
        public string QuestSpeech { get; set; }

        #endregion

        #region 好感度系统

        
        public List<string> Favors { get; set; } = new List<string>();
        
        public List<string> FavorDialogs { get; set; } = new List<string>();
        
        public List<string> Moods { get; set; } = new List<string>();
        
        public List<string> FavorLevels { get; set; } = new List<string>();
        
        public List<string> ItemInfos { get; set; } = new List<string>();
        public string PreferItem { get; set; }
        public string PreferItemGroup { get; set; }
        public string UnpreferItem { get; set; }
        public string UnpreferItemGroup { get; set; }
        public int DefaultFavor { get; set; } = -1;
        public int MaxGiftPerDay { get; set; } = -1;
        public int FavorableRelationshipVersion { get; set; }
        public List<int> FavorLevelPoints { get; } = new List<int>();
        public List<int> FavorRatePerMood { get; } = new List<int>();
        public List<NpcGiftRule> PreferredGiftRules { get; } = new List<NpcGiftRule>();
        public List<NpcGiftRule> UnpreferredGiftRules { get; } = new List<NpcGiftRule>();

        #endregion

        #region 其他

        public string FieldRole { get; set; }
        public string Skill { get; set; }
        public string EquipmentList { get; set; }
        public string IntData { get; set; }
        public string StringData { get; set; }

        #endregion
        #region 解析

        public static NpcFile Parse(string content)
        {
            if (string.IsNullOrEmpty(content))
                return new NpcFile { Content = content ?? "", Root = new ScriptNode { Tag = "ROOT" } };

            var root = new ScriptParser().Parse(content);
            var npc = new NpcFile { Root = root, Content = content };

            foreach (var node in root.Children)
            {
                string data = node.DataItems.Count > 0 ? node.GetFirstDataContent(content).Trim() : "";
                switch (node.Tag.ToLowerInvariant())
                {
                    
                    case "name": npc.Names.Add(StripBacktick(data)); break;
                    case "field name": npc.FieldName = StripBacktick(data); break;
                    case "role": npc.Role = data; break;

                    
                    case "field animation": npc.FieldAnimation = StripBacktick(data); break;
                    case "look around": npc.LookAround = ParseInt(data); break;
                    case "popup face": npc.PopupFace = data; break;
                    case "small face": npc.SmallFace = data; break;
                    case "big face": npc.BigFace = data; break;
                    case "img": npc.Img = data; break;
                    case "click area": npc.ClickArea = data; break;

                    
                    case "field wav": npc.FieldWav = StripBacktick(data); break;
                    case "popup wav": npc.PopupWav = data; break;

                    
                    case "dialog": npc.Dialogs.Add(data); break;
                    case "balloon message": npc.BalloonMessage = data; break;
                    case "quest speech": npc.QuestSpeech = data; break;

                    
                    case "favor": npc.Favors.Add(data); break;
                    case "favor_dialog": npc.FavorDialogs.Add(data); break;
                    case "mood": npc.Moods.Add(data); break;
                    case "favor level": npc.FavorLevels.Add(data); break;
                    case "item info": npc.ItemInfos.Add(data); break;
                    case "prefer item": npc.PreferItem = data; break;
                    case "prefer item group": npc.PreferItemGroup = data; break;
                    case "unprefer item": npc.UnpreferItem = data; break;
                    case "unprefer item group": npc.UnpreferItemGroup = data; break;
                    case "default favor": npc.DefaultFavor = ParseInt(data); break;
                    case "max gift per day": npc.MaxGiftPerDay = ParseInt(data); break;
                    case "gift item": ParseGiftItem(node, content, npc); break;
                    case "favorable relationship":
                        npc.FavorableRelationshipVersion = Math.Max(
                            npc.FavorableRelationshipVersion,
                            1);
                        ParseFavorableRelationship(node, content, npc);
                        break;
                    case "favorable relationship 2":
                        npc.FavorableRelationshipVersion = Math.Max(
                            npc.FavorableRelationshipVersion,
                            2);
                        ParseFavorableRelationship(node, content, npc);
                        break;

                    
                    case "field role": npc.FieldRole = data; break;
                    case "skill": npc.Skill = data; break;
                    case "equipment list": npc.EquipmentList = data; break;
                    case "int data": npc.IntData = data; break;
                    case "string data": npc.StringData = data; break;
                }
            }

            return npc;
        }

        private static void ParseFavorableRelationship(
            ScriptNode relationshipNode,
            string content,
            NpcFile npc)
        {
            foreach (var node in relationshipNode.Children)
            {
                var data = node.DataItems.Count > 0
                    ? node.GetFirstDataContent(content).Trim()
                    : string.Empty;
                switch (node.Tag.ToLowerInvariant())
                {
                    case "default favor":
                        npc.DefaultFavor = ParseInt(data);
                        break;
                    case "max gift per day":
                        npc.MaxGiftPerDay = ParseInt(data);
                        break;
                    case "gift item":
                        ParseGiftItem(node, content, npc);
                        break;
                    case "favor level point":
                        npc.FavorLevelPoints.AddRange(
                            PvfScriptValueReader.ReadIntegers(node, content));
                        break;
                }
            }
        }

        private static void ParseGiftItem(ScriptNode giftNode, string content, NpcFile npc)
        {
            foreach (var node in giftNode.Children)
            {
                switch (node.Tag.ToLowerInvariant())
                {
                    case "favor rate per mood":
                        npc.FavorRatePerMood.AddRange(PvfScriptValueReader.ReadIntegers(node, content));
                        break;
                    case "prefer item":
                        ParseGiftRules(node, content, npc.PreferredGiftRules, false, true);
                        break;
                    case "prefer item group":
                        ParseGiftRules(node, content, npc.PreferredGiftRules, true, true);
                        break;
                    case "unprefer item":
                        ParseGiftRules(node, content, npc.UnpreferredGiftRules, false, false);
                        break;
                    case "unprefer item group":
                        ParseGiftRules(node, content, npc.UnpreferredGiftRules, true, false);
                        break;
                }
            }
        }

        private static void ParseGiftRules(
            ScriptNode node,
            string content,
            ICollection<NpcGiftRule> destination,
            bool groupRule,
            bool preferred)
        {
            var tokens = new List<string>();
            foreach (var item in node.DataItems)
                tokens.AddRange(ScriptValueTokenizer.Tokenize(item.GetContent(content)));

            var width = preferred || tokens.Count % 3 != 0 ? 4 : 3;
            for (var index = 0; index + width - 1 < tokens.Count; index += width)
            {
                if (!int.TryParse(tokens[index], out var favorLevel))
                    continue;

                var rule = new NpcGiftRule
                {
                    FavorLevel = favorLevel,
                    MinimumCount = width == 4
                        ? ParseOptionalInteger(tokens, index + 2)
                        : 0,
                    FavorPointChange = width == 4
                        ? ParseOptionalInteger(tokens, index + 3)
                        : ParseOptionalInteger(tokens, index + 2),
                };

                if (groupRule)
                {
                    rule.ItemGroupName = tokens[index + 1];
                }
                else if (!int.TryParse(tokens[index + 1], out var itemId))
                {
                    continue;
                }
                else
                {
                    rule.ItemId = itemId;
                }

                destination.Add(rule);
            }
        }

        private static int ParseOptionalInteger(IReadOnlyList<string> tokens, int index)
            => index >= 0
                && index < tokens.Count
                && int.TryParse(tokens[index], out var value)
                    ? value
                    : 0;

        #endregion
    }

    public sealed class NpcGiftRule
    {
        public int FavorLevel { get; set; }
        public int ItemId { get; set; }
        public string ItemGroupName { get; set; }
        public int MinimumCount { get; set; }
        public int FavorPointChange { get; set; }
    }
}
