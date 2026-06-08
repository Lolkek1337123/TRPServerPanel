using System.Collections.Generic;

namespace TRPServerPanel.Services
{
    public static class CommandLibrary
    {
        public static List<object> GetCommands(string framework)
        {
            var groups = new List<object>();

            // --- 01. ENGINE CORE (Native Rust) ---
            groups.Add(new
            {
                title = "01. ENGINE CORE",
                color = "blue-500",
                items = new[] {
                    new { cmd = "status", ru = "Статус сервера", en = "Server Status" },
                    new { cmd = "server.save", ru = "Принудительное сохранение", en = "Force save world" },
                    new { cmd = "server.writecfg", ru = "Записать конфигурацию", en = "Write config files" },
                    new { cmd = "global.restart", ru = "Перезагрузка (300с таймер)", en = "Restart with 300s timer" },
                    new { cmd = "global.kickall", ru = "Кикнуть всех игроков", en = "Kick everyone" },
                    new { cmd = "global.banlist", ru = "Список забаненных", en = "Banned users list" },
                    new { cmd = "global.players", ru = "Список игроков", en = "Connected players list" },
                    new { cmd = "global.say", ru = "Сообщение от сервера", en = "Broadcast message" },
                    new { cmd = "global.teleport", ru = "Телепорт (Syntax: name ID)", en = "Teleport to name" },
                    new { cmd = "inventory.give", ru = "Выдать предмет себе", en = "Give item to self" },
                    new { cmd = "chat.say", ru = "Сообщение в чат", en = "Chat message" }
                }
            });

            // --- 02. MODDING FRAMEWORKS ---
            if (framework == "OXIDE")
            {
                groups.Add(new
                {
                    title = "02. OXIDE FRAMEWORK",
                    color = "red-500",
                    items = new[] {
                        new { cmd = "oxide.load", ru = "Загрузить плагин", en = "Load plugin" },
                        new { cmd = "oxide.unload", ru = "Выгрузить плагин", en = "Unload plugin" },
                        new { cmd = "oxide.reload", ru = "Перезагрузить плагин", en = "Reload plugin" },
                        new { cmd = "oxide.version", ru = "Версия Oxide", en = "Oxide version" },
                        new { cmd = "oxide.plugins", ru = "Список плагинов", en = "List plugins" },
                        new { cmd = "oxide.show groups", ru = "Список групп", en = "Show groups" },
                        new { cmd = "oxide.show perms", ru = "Список прав на сервере", en = "Show permissions" },
                        new { cmd = "oxide.grant user", ru = "Выдать право игроку", en = "Grant user permission" },
                        new { cmd = "oxide.revoke user", ru = "Отобрать право", en = "Revoke user permission" },
                        new { cmd = "oxide.group add", ru = "Добавить группу", en = "Create group" },
                        new { cmd = "oxide.usergroup add", ru = "Игрок в группу", en = "Add user to group" }
                    }
                });
            }
            else if (framework == "CARBON")
            {
                groups.Add(new
                {
                    title = "02. CARBON FRAMEWORK",
                    color = "red-500",
                    items = new[] {
                        new { cmd = "c.load", ru = "Загрузить плагины (* для всех)", en = "Load plugins" },
                        new { cmd = "c.unload", ru = "Выгрузить плагины", en = "Unload plugins" },
                        new { cmd = "c.reload", ru = "Перезагрузить плагины", en = "Reload plugins" },
                        new { cmd = "c.version", ru = "Версия Carbon/Rust", en = "Carbon version" },
                        new { cmd = "c.plugins", ru = "Список плагинов Carbon", en = "Carbon plugins list" },
                        new { cmd = "c.grant", ru = "Выдать права (Admin)", en = "Grant perms" },
                        new { cmd = "c.revoke", ru = "Отобрать права", en = "Revoke perms" },
                        new { cmd = "c.group", ru = "Управление группами", en = "Group management" },
                        new { cmd = "c.usergroup", ru = "Игрок в группу", en = "Add user to group" },
                        new { cmd = "c.editconfig", ru = "Открыть редактор конфигов", en = "Open config editor" },
                        new { cmd = "c.reloadconfig", ru = "Перезагрузить конфиг плагина", en = "Reload plugin config" },
                        new { cmd = "c.createplugin", ru = "Создать шаблон плагина", en = "Create new plugin" },
                        new { cmd = "c.aliases", ru = "Список всех алиасов команд", en = "List of all aliases" },
                        new { cmd = "c.assignalias", ru = "Назначить алиас команды", en = "Assign command alias" },
                        new { cmd = "c.craftingspeedmultiplier_wb1", ru = "Скорость крафта (Верстак 1)", en = "Craft speed (WB1)" },
                        new { cmd = "c.craftingspeedmultiplier_wb2", ru = "Скорость крафта (Верстак 2)", en = "Craft speed (WB2)" },
                        new { cmd = "c.craftingspeedmultiplier_wb3", ru = "Скорость крафта (Верстак 3)", en = "Craft speed (WB3)" },
                        new { cmd = "c.custommapname", ru = "Название карты в браузере", en = "Custom map name" },
                        new { cmd = "c.debug", ru = "Уровень дебаг-логов (-1 выкл)", en = "Debug logging level" },
                        new { cmd = "c.debughook", ru = "Дебаг конкретного хука", en = "Debug specific hook" },
                        new { cmd = "c.defaultserverchatcolor", ru = "Цвет чата сервера", en = "Default chat color" },
                        new { cmd = "c.defaultserverchatname", ru = "Имя сервера в чате", en = "Default chat name" },
                        new { cmd = "c.find", ru = "Поиск по консольным командам", en = "Find console command" },
                        new { cmd = "c.findchat", ru = "Поиск по чат-командам", en = "Find chat command" },
                        new { cmd = "c.gocommunity", ru = "Оптимизация под Community", en = "Optimize for Community" },
                        new { cmd = "c.installplugin", ru = "Установить плагин из бэкапа", en = "Install from backup" },
                        new { cmd = "c.logfiletype", ru = "Тип лог-файла (0-2)", en = "Log file type" },
                        new { cmd = "c.mixingspeedmultiplier", ru = "Скорость стола смешивания", en = "Mixing speed multiplier" },
                        new { cmd = "c.notechtreeunlock", ru = "Заблокировать дерево техн.", en = "Block tech tree" },
                        new { cmd = "c.ovenspeedmultiplier", ru = "Скорость печей", en = "Oven speed multiplier" }
                    }
                });

                // --- 03. CARBON SYSTEM & CORE ---
                groups.Add(new
                {
                    title = "03. CARBON SYSTEM",
                    color = "orange-500",
                    items = new[] {
                        new { cmd = "c.build", ru = "Информация о сборке Carbon", en = "Carbon build info" },
                        new { cmd = "c.extensions", ru = "Список расширений", en = "Loaded extensions" },
                        new { cmd = "c.modules", ru = "Список модулей", en = "Available modules" },
                        new { cmd = "c.hooks", ru = "Информация о хуках", en = "Active hooks info" },
                        new { cmd = "c.profile", ru = "Профилирование Mono", en = "Toggle mono profiling" },
                        new { cmd = "c.whymodded", ru = "Анализ статуса 'Modded'", en = "Why modded analysis" },
                        new { cmd = "c.shutdown", ru = "Выгрузить Carbon", en = "Unload Carbon" },
                        new { cmd = "c.migrate_perms_proto", ru = "Миграция прав в Protobuf", en = "Migrate to Protobuf" },
                        new { cmd = "c.logfiletype", ru = "Режим логирования", en = "Logging mode" }
                    }
                });
            }

            // --- 04. SERVER VARIABLES (ConVars) ---
            groups.Add(new
            {
                title = "04. SERVER VARIABLES",
                color = "emerald-500",
                items = new[] {
                    new { cmd = "server.hostname", ru = "Название сервера", en = "Server Hostname" },
                    new { cmd = "server.maxplayers", ru = "Макс. игроков", en = "Max Players" },
                    new { cmd = "server.saveinterval", ru = "Интервал сохранения", en = "Save Interval" },
                    new { cmd = "fps.limit", ru = "Лимит FPS", en = "FPS Limit" },
                    new { cmd = "fps.graph", ru = "График производительности", en = "Performance graph" },
                    new { cmd = "decay.scale", ru = "Множитель гниения", en = "Decay Scale" },
                    new { cmd = "craft.instant", ru = "Мгновенный крафт", en = "Instant Craft" },
                    new { cmd = "antihack.enabled", ru = "Антихак Вкл/Выкл", en = "Antihack switch" },
                    new { cmd = "antihack.userlevel", ru = "Уровень проверки antihack", en = "Antihack user level" },
                    new { cmd = "ai.think", ru = "ИИ животных Вкл/Выкл", en = "AI Switch" },
                    new { cmd = "physics.steps", ru = "Шаги физики", en = "Physics steps" },
                    new { cmd = "env.day", ru = "Установить день", en = "Set day" },
                    new { cmd = "env.month", ru = "Установить месяц", en = "Set month" },
                    new { cmd = "env.year", ru = "Установить год", en = "Set year" },
                    new { cmd = "server.secure", ru = "Режим EAC (Security)", en = "EAC Mode" }
                }
            });

            return groups;
        }
    }
}
