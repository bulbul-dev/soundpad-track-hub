using System.Windows.Controls;

namespace SoundpadTrackHub.Views
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();
        }
    }

    public partial class SecurityPage : UserControl
    {
        public SecurityPage()
        {
            InitializeComponent();
        }
    }

    public static class SecurityTexts
    {
        public const string Https = "Только HTTPS. Загрузки по незащищённому HTTP блокируются.";
        public const string Host = "Только ru.hitmoz.org: файл скачивается с ru.hitmoz.org или с файлового сервера, на который ru.hitmoz.org перенаправил по своей ссылке /get/…mp3 после вашего клика.";
        public const string Mp3 = "Только .mp3. После загрузки проверяется содержимое: веб-страницы и переименованные программы отклоняются.";
        public const string Blocked = "Исполняемые и скриптовые файлы блокируются: exe, bat, cmd, com, scr, dll, ps1, vbs, js, msi, lnk, reg, hta и другие.";
        public const string Confirm = "Перед каждой загрузкой — подтверждение с именем файла, ссылкой и папкой сохранения.";
        public const string Overwrite = "Существующий файл не перезаписывается без отдельного подтверждения — по умолчанию сохраняется копия с номером.";
        public const string Temp = "Файл сначала скачивается во временную папку. При отмене или ошибке временный файл удаляется.";
        public const string Search = "Вкладка «Поиск» открывает страницу поиска ru.hitmoz.org во встроенном браузере и показывает найденные треки списком. Слушать — плеером сайта, скачивать — штатной ссылкой сайта.";
        public const string SoundpadApi = "Треки добавляются в категорию Soundpad через его официальный интерфейс удалённого управления (именованный канал sp_remote_control).";
        public const string Popups = "Всплывающие окна и переходы на внешние сайты (реклама) внутри Track Hub блокируются.";

        public const string NoBypass = "Не обходит Cloudflare, CAPTCHA, авторизацию и другие ограничения сайта. Если сайт отвечает HTTP 403 — показывается экран ошибки.";
        public const string NoScraping = "Не делает HTTP-запросов к Hitmoz в обход браузера и не извлекает аудиофайлы из плеера страницы. Поиск читает список треков только со страницы, открытой во встроенном браузере.";
        public const string NoInjection = "Не внедряет код в Soundpad, не использует DLL injection, не изменяет и не патчит Soundpad.exe и файлы Soundpad.";
        public const string NoVolume = "Не меняет системную громкость Windows. Настройку Soundpad «Нормализация громкости» меняет только когда вы выбираете режим громкости — и перезапускает Soundpad только с вашего подтверждения.";
        public const string NoMock = "Не показывает демо-треки вместо настоящего сайта.";

        public const string Rights = "Скачивайте только те треки, которые вы имеете право использовать. Воспроизведение музыки в голосовых чатах, на стримах и в публичных местах может требовать разрешения правообладателя. Ответственность за использование треков несёт пользователь.";
    }
}
