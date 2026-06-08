using System.Collections.Generic;

namespace TRPServerPanel.Utils
{
    public static class RustItems
    {
        private static readonly Dictionary<int, (string En, string Ru)> ItemMap = new()
        {
            { 1545779598, ("Assault Rifle", "Штурмовая винтовка (AK-47)") },
            { 1588298435, ("Bolt Action Rifle", "Снайперская винтовка (Болт)") },
            { 1796682209, ("Custom SMG", "Пистолет-пулемет (SMG)") },
            { -1758372725, ("Thompson", "Пистолет-пулемет Томпсона") },
            { 1318558775, ("MP5A4", "MP5A4") },
            { -765183617, ("Double Barrel Shotgun", "Двуствольный дробовик") },
            { 795371088, ("Pump Shotgun", "Помповый дробовик") },
            { 649912614, ("Revolver", "Револьвер") },
            { 1373971859, ("Python Revolver", "Револьвер Питон") },
            { 818877484, ("Semi-Automatic Pistol", "Полуавтоматический пистолет (SAP)") },
            { -904863145, ("Semi-Automatic Rifle", "Полуавтоматическая винтовка (SAR)") },
            { 442886268, ("Rocket Launcher", "Ракетница") },
            { 1248356124, ("Timed Explosive Charge (C4)", "Таймерная взрывчатка (C4)") },
            { -1878475007, ("Satchel Charge", "Сумка с зарядами (Сачель)") },
            { 143803535, ("F1 Grenade", "Граната F1") },
            { -2139580305, ("Auto Turret", "Автоматическая турель") },
            { -2067472972, ("Sheet Metal Door", "Металлическая дверь") },
            { 1390353317, ("Sheet Metal Double Door", "Двойная металлическая дверь") },
            { 1948067030, ("Ladder Hatch", "Люк с лестницей") },
            { 1391583329, ("Code Lock", "Кодовый замок") },
            { -1211166256, ("5.56 Rifle Ammo", "Патроны 5.56 мм") },
            { -967648160, ("High External Stone Wall", "Высокая каменная стена") },
            { -2002277461, ("Road Sign Jacket", "Жилет из дорожных знаков") },
            { 1850456855, ("Road Sign Kilt", "Юбка из дорожных знаков") },
            { -194953424, ("Metal Facemask", "Металлическая маска") },
            { 1110385766, ("Metal Chest Plate", "Металлический нагрудник") },
            { -803263829, ("Coffee Can Helmet", "Шлем из кофейной банки") }
        };

        public static string GetItemName(int itemId, string language = "ru")
        {
            if (ItemMap.TryGetValue(itemId, out var names))
            {
                return language == "ru" ? names.Ru : names.En;
            }
            return $"Item [{itemId}]";
        }

        public static string GetItemNameFormatted(int itemId)
        {
            if (ItemMap.TryGetValue(itemId, out var names))
            {
                return $"{names.En}|{names.Ru}";
            }
            return $"Item [{itemId}]|Предмет [{itemId}]";
        }
    }
}
