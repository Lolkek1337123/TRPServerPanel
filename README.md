# 🖥️ TRP Server Panel

🌐 **[Русский](#russian) | [English](#english)**

---

<a name="russian"></a>
## Описание (RU)

**TRP Server Panel** — это профессиональный десктопный менеджер для создания, администрирования и мониторинга выделенных серверов игры **Rust**. Панель предоставляет единую экосистему управления с современным стеклянным интерфейсом (Glassmorphism), встроенными инструментами телеметрии, менеджером плагинов и ИИ-помощником.

### 🌟 Основные Возможности

#### 🚀 1. Управление Серверами (Server Lifecycle)
* **Установка в 1 клик**: Автоматическая загрузка `SteamCMD` и развертывание файлов чистого сервера Rust (`app_update 258550`).
* **Framework Integration**: Установка и автоматическое обновление серверных модовых ядер:
  * **Oxide** (классический фреймворк).
  * **Carbon** (модернизированный C# загрузчик).
* **Мониторинг процессов**: Защита от зависаний, автоматический перезапуск упавшего сервера, корректный перехват Standard Output процесса `RustDedicated.exe`.

#### 📊 2. Живая Телеметрия и Мониторинг
* **Графики нагрузки (Chart.js)**: Мониторинг ресурсов вашего хостинга в реальном времени.
* **Показатели**:
  * Загрузка процессора (CPU)
  * Использование оперативной памяти (RAM)
  * Свободное дисковое пространство (SSD/HDD)
  * Активный пинг и статус сервера (A2S Query).

#### 🖥️ 3. Интерактивная RCON-Консоль
* **Мгновенная связь**: Выполнение консольных команд без задержек.
* **Категоризированные логи**: Цветовая разметка вывода консоли (ошибки, чат, системные предупреждения).
* **Быстрые кнопки**: Запуск предустановленных команд (статус, кик всех, ручной сейв).

#### 🔌 4. Умный Менеджер Плагинов
* **Удобный поиск и фильтрация**: Отображение всех C# плагинов в директории мода.
* **Горячие клавиши**: Установка, обновление, отключение (перенос в backup) и удаление плагинов прямо из панели без ручной работы с файловым менеджером.

#### 🤖 5. ИИ-Помощник (Gemini AI Copilot)
* **Интегрированный чат**: Встроенный помощник на базе Google Gemini для ответа на технические вопросы.
* **Помощь**: Настройка конфигов, исправление ошибок компиляции плагинов, генерация команд и оптимизация параметров запуска сервера.

#### 💾 6. Бэкапы и Планировщик Вайпов
* **Менеджер резервных копий**: Архивация карты (.map), пользовательских баз данных (.db) и конфигураций.
* **Автоматические вайпы**: Тонкая настройка удаления файлов карты и чертежей по расписанию (Wipe Scheduler).

### 📁 Структура установленного сервера

```text
C:\YourServerFolder\
├── RustDedicated.exe          # Исполняемый процесс игры
├── server_cfg.json            # Настройки панели (порты, название, сид)
├── steamcmd/                  # Встроенный загрузчик Valve
│   └── steamcmd.exe
├── RustDedicated_Data/        # Системные файлы игры
└── oxide/ или carbon/         # Каталог модификаций
    ├── plugins/               # Папка для C# плагинов
    ├── config/                # Конфигурационные файлы
    ├── data/                  # Базы данных плагинов
    └── logs/                  # Логи ядра
```

### 🛠️ Системные Требования
* **ОС**: Windows 10 / 11 (64-bit), Windows Server 2019 / 2022.
* **Процессор**: Intel Core i5/i7/i9 или AMD Ryzen с высокой тактовой частотой (рекомендуется от 3.6 GHz).
* **Оперативная память**: Минимум 12 ГБ (для сервера Rust + Панели).
* **WebView2 Runtime**: Требуется для отрисовки графиков и дашборда (устанавливается автоматически при запуске).

### 🚀 Быстрый Старт
1. Перейдите во вкладку [Releases](https://github.com/Lolkek1337123/TRPServerPanel/releases).
2. Скачайте архив `TRPServerPanel_Release.zip`.
3. Распакуйте архив в удобное место на диске.
4. Запустите файл `TRPServerPanel.exe`.
5. Укажите пустую папку на компьютере, куда будет установлен сервер Rust, и нажмите **«Установить»**.

---

<a name="english"></a>
## Description (EN)

**TRP Server Panel** is a professional desktop manager for creating, administering, and monitoring **Rust** dedicated servers. The panel provides a unified management ecosystem with a modern Glassmorphism UI, built-in telemetry tools, a plugin manager, and an AI assistant.

### 🌟 Key Features

#### 🚀 1. Server Lifecycle Management
* **1-Click Installation**: Automatic download of `SteamCMD` and deployment of clean Rust server files (`app_update 258550`).
* **Framework Integration**: Install and automatically update server mod frameworks:
  * **Oxide** (classic framework).
  * **Carbon** (modern C# loader).
* **Process Monitoring**: Crash protection, automatic server restarts, and proper capturing of `RustDedicated.exe` Standard Output.

#### 📊 2. Live Telemetry & Monitoring
* **Resource Graphs (Chart.js)**: Real-time monitoring of host resources.
* **Metrics**:
  * CPU Usage
  * RAM Usage
  * Free Storage (SSD/HDD)
  * Active Ping and Server Status (A2S Query).

#### 🖥️ 3. Interactive RCON Console
* **Instant Connection**: Execute console commands with zero delay.
* **Categorized Logs**: Color-coded console output (errors, chat, system warnings).
* **Quick Action Buttons**: Fast execution of pre-configured commands (status, kick all, manual save).

#### 🔌 4. Smart Plugin Manager
* **Easy Search & Filter**: Displays all C# plugins in the mod directory.
* **Hot Actions**: Install, update, disable (move to backup), and delete plugins directly from the panel.

#### 🤖 5. Gemini AI Copilot
* **Built-in Chat**: Direct assistance using Google Gemini.
* **Features**: Edit configs, fix compiler errors, generate commands, and optimize server launch arguments.

#### 💾 6. Backups & Wipe Scheduler
* **Backup Manager**: Archive map files (.map), plugin database files (.db), and configurations.
* **Automatic Wipes**: Schedule map and blueprint wipes (Wipe Scheduler).

### 📁 Installed Server Directory Structure

```text
C:\YourServerFolder\
├── RustDedicated.exe          # Main game process
├── server_cfg.json            # Panel config (ports, hostname, seed)
├── steamcmd/                  # Built-in SteamCMD loader
│   └── steamcmd.exe
├── RustDedicated_Data/        # Core game data
└── oxide/ or carbon/         # Modification directory
    ├── plugins/               # Put C# plugins here
    ├── config/                # Plugin config files
    ├── data/                  # Plugin databases
    └── logs/                  # Mod engine logs
```

### 🛠️ System Requirements
* **OS**: Windows 10 / 11 (64-bit), Windows Server 2019 / 2022.
* **CPU**: Intel Core i5/i7/i9 or AMD Ryzen with high clock speed (3.6+ GHz recommended).
* **RAM**: Minimum 12 GB (for Rust server + Panel).
* **WebView2 Runtime**: Required for rendering UI and graphs (automatically installed on launch).

### 🚀 Quick Start
1. Go to the [Releases](https://github.com/Lolkek1337123/TRPServerPanel/releases) page.
2. Download the `TRPServerPanel_Release.zip` archive.
3. Unzip the archive to any folder on your computer.
4. Run `TRPServerPanel.exe`.
5. Select an empty folder where you want the Rust server to be installed, and click **"Install"**.

---

**TEAM_RUST_PLUGINS — TRP Perfect Standard**
