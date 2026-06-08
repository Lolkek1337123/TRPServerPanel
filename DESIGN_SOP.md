# TRP Server Panel — Регламент работы (SOP)

Данный документ описывает внутреннюю логику, структуру файлов и процесс настройки серверов в новой платформе.

## 📁 Структура Серверной Папки
После установки каждого сервера, в выбранной директории создается следующая структура:
- `RustDedicated.exe`: Основной исполняемый файл игры.
- `server_cfg.json`: Центр управления (создается автоматически).
- `steamcmd/`: Встроенный загрузчик Steam.
- `RustDedicated_Data/`: Данные ядра.
- `Carbon/` или `Oxide/`: Папки выбранных фреймворков.

## 📁 Пример структуры (Desktop/fffffffff)
После успешной установки **Rust Core + Oxide** ваша папка будет выглядеть так:

```text
C:\Users\SteveMarkins\Desktop\fffffffff\
├── RustDedicated.exe          # Главный процесс сервера
├── server_cfg.json            # Настройки панели (Hostname, Ports)
├── steamcmd.exe               # Утилита обновления (в папке steamcmd/)
├── RustDedicated_Data/        # Системные файлы игры
│   └── Managed/
│       └── Oxide.Rust.dll     # Ядро Oxide (Патч)
└── oxide/                     # Папка управления модами
    ├── plugins/               # Сюда класть .cs плагины
    ├── config/                # Настройки плагинов (JSON)
    ├── data/                  # Базы данных плагинов
    ├── lang/                  # Переводы
    └── logs/                  # Логи Oxide
```

## 🛠️ Настройка через server_cfg.json
Этот файл является единственным источником правды для запуска.
```json
{
  "Hostname": "TRP_TESTSERVER",
  "Port": 28015,
  "RconPort": 28017,
  "RconPassword": "changeme",
  "WorldSize": 3000,
  "Seed": 12345,
  "MaxPlayers": 100,
  "AdditionalArgs": "-batchmode -nographics"
}
```
> [!TIP]
> Вы можете редактировать этот файл вручную или через будущий интерфейс настроек в панели.

## 🚀 Процесс развертывания (Installation Flow)
1.  **SteamCMD Setup**: Загрузка и распаковка официального инструмента Valve.
2.  **Core Sync**: Команда `app_update 258550 validate` для загрузки Rust.
3.  **Core Verification**: Панель проверяет наличие `RustDedicated.exe`. Если файла нет — установка прерывается с ошибкой.
4.  **Framework Patching**: Распаковка батников и DLL для Oxide или Carbon поверх ядра.
5.  **Initialization**: Генерация файла `server_cfg.json`.

## 🎮 Логика запуска (Startup Logic)
При нажатии кнопки **START**:
1.  **Cleanup**: Очистка «зомби-процессов» `RustDedicated` именно в этой папке.
2.  **Config Load**: Чтение `server_cfg.json` и синхронизация с интерфейсом.
3.  **Arg Builder**: Сборка строки запуска командной строки (CMD Arguments).
4.  **PID Tracking**: Запуск процесса и перехват `StandardOutput` для трансляции в нашу консоль.

---
**TEAM_RUST_PLUGINS — TRP Perfect Standard**
