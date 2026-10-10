using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace ClipVoice.Services;

/// <summary>Applies the selected UI language while keeping each control's source text.</summary>
public static class LocalizationService
{
    private sealed class OriginalText
    {
        public string? Text;
        public string? Content;
        public string? Header;
        public string? ToolTip;
        public string? Title;
    }

    private static readonly ConditionalWeakTable<DependencyObject, OriginalText> Originals = new();

    private static readonly Dictionary<string, (string Ru, string Uk)> Texts = new(StringComparer.Ordinal)
    {
        ["Interface language"] = ("Язык интерфейса", "Мова інтерфейсу"),
        ["GENERAL"] = ("ОСНОВНОЕ", "ЗАГАЛЬНЕ"), ["HOTKEYS"] = ("ГОРЯЧИЕ КЛАВИШИ", "ГАРЯЧІ КЛАВІШІ"),
        ["SPEECH & TRANSLATION"] = ("ОЗВУЧИВАНИЕ И ПЕРЕВОД", "ОЗВУЧЕННЯ ТА ПЕРЕКЛАД"), ["TEMPLATES"] = ("ШАБЛОНЫ", "ШАБЛОНИ"),
        ["ClipVoice Settings"] = ("Настройки ClipVoice", "Налаштування ClipVoice"),
        ["Configure the main features here. No API keys or account registration required."] = ("Настройте основные функции приложения. API-ключи и регистрация не требуются.", "Налаштуйте основні функції програми. API-ключі та реєстрація не потрібні."),
        ["Read selected text aloud"] = ("Озвучивание выделенного текста", "Озвучення виділеного тексту"),
        ["Select text, open the template context menu, and choose “Read selected text aloud”."] = ("Выделите текст, откройте контекстное меню шаблонов и выберите «Озвучить выделенный текст».", "Виділіть текст, відкрийте контекстне меню шаблонів і виберіть «Озвучити виділений текст»."),
        ["How to open the panel"] = ("Как открывать панель", "Як відкривати панель"),
        ["If the side handle gets in the way, choose the first option. Nothing will remain at the screen edge, and ClipVoice will open only with its global hotkey."] = ("Если боковая полоска мешает, выберите первый вариант. Панель будет открываться только глобальной горячей клавишей.", "Якщо бічна смужка заважає, виберіть перший варіант. Панель відкриватиметься лише глобальною гарячою клавішею."),
        ["Hotkey only — no side handle"] = ("Только горячая клавиша — без полоски", "Лише гаряча клавіша — без бічної смужки"),
        ["Hotkey + side handle"] = ("Горячая клавиша + боковая полоска", "Гаряча клавіша + бічна смужка"),
        ["Change the panel hotkey in the HOTKEYS tab."] = ("Горячую клавишу панели можно изменить во вкладке «ГОРЯЧИЕ КЛАВИШИ».", "Гарячу клавішу панелі можна змінити у вкладці «ГАРЯЧІ КЛАВІШІ»."),
        ["Hide ClipVoice after pasting"] = ("Скрывать ClipVoice после вставки", "Приховувати ClipVoice після вставлення"),
        ["Hide the panel automatically when clicking outside it"] = ("Автоматически скрывать панель при клике вне её", "Автоматично приховувати панель після клацання поза нею"),
        ["Startup and storage"] = ("Запуск и хранение", "Запуск і зберігання"), ["Start with Windows"] = ("Запускать вместе с Windows", "Запускати разом із Windows"),
        ["Open audio cache folder"] = ("Открыть папку аудиокэша", "Відкрити теку аудіокешу"),
        ["How to assign a hotkey"] = ("Как назначить горячую клавишу", "Як призначити гарячу клавішу"),
        ["Click a field and press the desired shortcut; you do not need to type it. Backspace/Delete clears the field. A global hotkey must include Ctrl, Alt, Shift, or Win."] = ("Нажмите на поле и нажмите нужное сочетание — вводить его вручную не нужно. Backspace/Delete очищает поле. Глобальная клавиша должна включать Ctrl, Alt, Shift или Win.", "Натисніть поле та потрібне сполучення — вводити його вручну не потрібно. Backspace/Delete очищає поле. Глобальна клавіша має містити Ctrl, Alt, Shift або Win."),
        ["Show / hide panel"] = ("Открыть / скрыть панель", "Відкрити / приховати панель"),
        ["Shows history and templates over the current window."] = ("Показывает историю и шаблоны поверх текущего окна.", "Показує історію та шаблони поверх поточного вікна."),
        ["Read aloud"] = ("Озвучить", "Озвучити"), ["Translate automatically"] = ("Перевести автоматически", "Перекласти автоматично"),
        ["Translate and speak"] = ("Перевести и озвучить", "Перекласти й озвучити"),
        ["The language is detected from the text, not the keyboard layout."] = ("Язык определяется по тексту, а не по раскладке клавиатуры.", "Мова визначається за текстом, а не за розкладкою клавіатури."),
        ["The translation is copied and read aloud immediately."] = ("Перевод копируется в буфер и сразу озвучивается.", "Переклад копіюється в буфер і одразу озвучується."),
        ["Restore default shortcuts"] = ("Восстановить сочетания по умолчанию", "Відновити стандартні сполучення"),
        ["Speech status"] = ("Состояние озвучивания", "Стан озвучення"), ["Last: —"] = ("Последнее: —", "Останнє: —"),
        ["Edge online"] = ("Edge онлайн", "Edge онлайн"), ["Google fallback"] = ("Резерв Google", "Резерв Google"), ["Piper local"] = ("Piper локально", "Piper локально"),
        ["Memory: checking…"] = ("Память: проверка…", "Пам’ять: перевірка…"), ["Online speech"] = ("Онлайн-озвучивание", "Онлайн-озвучення"),
        ["Russian"] = ("Русский", "Російська"), ["Ukrainian"] = ("Украинский", "Українська"), ["English"] = ("Английский", "Англійська"),
        ["Speech rate"] = ("Скорость голоса", "Швидкість мовлення"), ["Automatic translation"] = ("Автоматический перевод", "Автоматичний переклад"),
        ["Translate foreign text into"] = ("Переводить иностранный текст на", "Перекладати іноземний текст на"),
        ["Used for English, French, and other languages."] = ("Используется для английского, французского и других языков.", "Використовується для англійської, французької та інших мов."),
        ["Example"] = ("Пример", "Приклад"), ["Russian / Ukrainian → English"] = ("Русский / украинский → английский", "Російська / українська → англійська"),
        ["Speech diagnostics"] = ("Диагностика озвучивания", "Діагностика озвучення"), ["Copy error log"] = ("Скопировать журнал ошибок", "Скопіювати журнал помилок"),
        ["Template menu"] = ("Меню шаблонов", "Меню шаблонів"), ["Shortcut"] = ("Сочетание", "Сполучення"),
        ["Folders"] = ("Папки", "Теки"), ["Drag to change the order"] = ("Перетащите, чтобы изменить порядок", "Перетягніть, щоб змінити порядок"),
        ["Click and drag"] = ("Зажмите и перетащите", "Затисніть і перетягніть"), ["Rename"] = ("Переименовать", "Перейменувати"), ["Delete"] = ("Удалить", "Видалити"),
        ["Import TXT"] = ("Импорт TXT", "Імпорт TXT"), ["Export TXT"] = ("Экспорт TXT", "Експорт TXT"), ["+ New template"] = ("+ Новый шаблон", "+ Новий шаблон"),
        ["Edit"] = ("Изменить", "Редагувати"), ["Cancel"] = ("Отмена", "Скасувати"), ["Save changes"] = ("Сохранить изменения", "Зберегти зміни"),
        ["CLIPBOARD"] = ("БУФЕР ОБМЕНА", "БУФЕР ОБМІНУ"), ["Latest 25"] = ("Последние 25", "Останні 25"), ["Clear"] = ("Очистить", "Очистити"),
        ["clipboard • translation • speech"] = ("буфер • перевод • озвучивание", "буфер • переклад • озвучення"), ["＋ New"] = ("＋ Новый", "＋ Новий"),
        ["▶ Speak"] = ("▶ Озвучить", "▶ Озвучити"), ["■ Stop"] = ("■ Стоп", "■ Стоп"), ["🌐 Translate"] = ("🌐 Перевести", "🌐 Перекласти"),
        ["Ready"] = ("Готово", "Готово"), ["TTS: checking…"] = ("TTS: проверка…", "TTS: перевірка…"),
        ["Create template"] = ("Создать шаблон", "Створити шаблон"), ["Template"] = ("Шаблон", "Шаблон"), ["Template name"] = ("Название шаблона", "Назва шаблону"),
        ["Folder"] = ("Папка", "Тека"), ["Template text"] = ("Текст шаблона", "Текст шаблону"), ["Save template"] = ("Сохранить шаблон", "Зберегти шаблон"),
        ["New folder"] = ("Новая папка", "Нова тека"), ["Create folder"] = ("Создать папку", "Створити теку"), ["Folder name:"] = ("Название папки:", "Назва теки:"),
        ["Rename folder"] = ("Переименовать папку", "Перейменувати теку"), ["New name:"] = ("Новое название:", "Нова назва:"),
        ["Manage templates"] = ("Управление шаблонами", "Керування шаблонами"), ["No templates yet"] = ("Шаблонов пока нет", "Шаблонів поки немає")
        , ["Settings"] = ("Настройки", "Налаштування"),
        ["Hide"] = ("Скрыть", "Приховати"),
        ["✓ Active   •   Ctrl+V — paste   •   double-click — paste now"] = ("✓ Активно • Ctrl+V — вставить • двойной клик — вставить сразу", "✓ Активно • Ctrl+V — вставити • подвійне клацання — вставити одразу"),
        ["Click here and press a new shortcut"] = ("Нажмите сюда и задайте новое сочетание", "Натисніть тут і задайте нове сполучення"),
        ["Compare engine speed"] = ("Сравнить скорость движков", "Порівняти швидкість рушіїв"),
        ["Prepare offline fallback"] = ("Подготовить локальный резерв", "Підготувати локальний резерв"),
        ["+ Folder"] = ("+ Папка", "+ Тека")
        , ["The copied text is too large for ClipVoice history."] = ("Скопированный текст слишком велик для истории ClipVoice.", "Скопійований текст завеликий для історії ClipVoice."),
        ["Text added to history"] = ("Текст добавлен в историю", "Текст додано до історії"), ["Files added to history"] = ("Файлы добавлены в историю", "Файли додано до історії"),
        ["Clipboard is temporarily unavailable: "] = ("Буфер обмена временно недоступен: ", "Буфер обміну тимчасово недоступний: "),
        ["The item is selected. Ctrl+V will paste it."] = ("Элемент выбран. Ctrl+V вставит его.", "Елемент вибрано. Ctrl+V вставить його."),
        ["Clipboard error: "] = ("Ошибка буфера обмена: ", "Помилка буфера обміну: "), ["Stopped"] = ("Остановлено", "Зупинено"),
        ["Preparing speech…"] = ("Подготовка озвучивания…", "Підготовка озвучення…"), ["Pasted"] = ("Вставлено", "Вставлено"),
        ["History cleared"] = ("История очищена", "Історію очищено"), ["Clipboard monitoring enabled"] = ("Отслеживание буфера включено", "Відстеження буфера увімкнено"),
        ["Clipboard monitoring paused"] = ("Отслеживание буфера приостановлено", "Відстеження буфера призупинено"),
        ["Settings saved"] = ("Настройки сохранены", "Налаштування збережено"),
        ["Some hotkeys are unavailable. Open Settings → Hotkeys."] = ("Некоторые горячие клавиши недоступны. Откройте Настройки → Горячие клавиши.", "Деякі гарячі клавіші недоступні. Відкрийте Налаштування → Гарячі клавіші."),
        ["Check all, including offline"] = ("Проверить всё, включая офлайн", "Перевірити все, включно з офлайн"),
        ["No selected text was found. Select text and reopen the template menu."] = ("Выделенный текст не найден. Выделите текст и снова откройте меню шаблонов.", "Виділений текст не знайдено. Виділіть текст і знову відкрийте меню шаблонів."),
        ["Enter a template name."] = ("Введите название шаблона.", "Введіть назву шаблону."),
        ["Template text cannot be empty."] = ("Текст шаблона не может быть пустым.", "Текст шаблону не може бути порожнім."),
        ["Select a folder."] = ("Выберите папку.", "Виберіть теку."), ["Enter a name for the new folder."] = ("Введите название новой папки.", "Введіть назву нової теки."),
        ["A folder with this name already exists. Select it from the list above."] = ("Папка с таким названием уже существует. Выберите её из списка выше.", "Тека з такою назвою вже існує. Виберіть її зі списку вище."),
        ["+ Create a new folder…"] = ("+ Создать новую папку…", "+ Створити нову теку…"),
        ["Content hidden"] = ("Содержимое скрыто", "Вміст приховано"), ["Secret template"] = ("Секретный шаблон", "Таємний шаблон"),
        ["Translate and speak selected text"] = ("Перевести и озвучить выделенный текст", "Перекласти й озвучити виділений текст"),
        ["ClipVoice automatically switches between two online engines and local Piper. ✓ means speech has succeeded; ◐ means Piper is installed but offline synthesis has not been verified."] = ("ClipVoice автоматически переключается между двумя онлайн-движками и локальным Piper. ✓ — озвучивание работает; ◐ — Piper установлен, но офлайн-синтез ещё не проверен.", "ClipVoice автоматично перемикається між двома онлайн-рушіями та локальним Piper. ✓ — озвучення працює; ◐ — Piper встановлено, але офлайн-синтез ще не перевірено."),
        ["After download, local Piper works without internet. Models stay on disk and load into RAM only while generating speech; the Piper process closes afterward."] = ("После загрузки Piper работает без интернета. Модели хранятся на диске и загружаются в память только во время озвучивания; затем процесс Piper закрывается.", "Після завантаження Piper працює без інтернету. Моделі зберігаються на диску та завантажуються в пам’ять лише під час озвучення; потім процес Piper закривається."),
        ["The primary speech engine is Edge Read Aloud, available online without an account or API key. If Edge is temporarily unavailable, ClipVoice automatically tries Google Translate TTS. Choose Russian, Ukrainian, and English Edge voices below; other languages and voices are selected automatically."] = ("Основной движок — Edge Read Aloud, работающий через интернет без аккаунта и API-ключа. Если Edge недоступен, ClipVoice попробует Google Translate TTS. Ниже можно выбрать голоса Edge для русского, украинского и английского; остальные языки и голоса выбираются автоматически.", "Основний рушій — Edge Read Aloud, що працює через інтернет без облікового запису й API-ключа. Якщо Edge недоступний, ClipVoice спробує Google Translate TTS. Нижче можна вибрати голоси Edge для російської, української та англійської; решта мов і голосів вибираються автоматично."),
        ["The selected engine applies the speech rate while generating audio; the player uses normal playback speed."] = ("Выбранный движок применяет скорость при создании аудио; плеер воспроизводит его с обычной скоростью.", "Вибраний рушій застосовує швидкість під час створення аудіо; програвач відтворює його зі звичайною швидкістю."),
        ["ClipVoice detects the language from the text itself. Russian or Ukrainian → English. English, French, German, and other foreign languages → the Russian or Ukrainian language selected below. Keyboard layout does not affect translation direction."] = ("ClipVoice определяет язык текста. Русский или украинский → английский. Английский, французский, немецкий и другие языки → выбранный ниже русский или украинский. Раскладка клавиатуры не влияет на направление перевода.", "ClipVoice визначає мову тексту. Російська або українська → англійська. Англійська, французька, німецька та інші мови → вибрана нижче російська або українська. Розкладка клавіатури не впливає на напрямок перекладу."),
        ["After translation, the result becomes the current clipboard content, ready for Ctrl+V. “Translate and speak” also reads the translated text aloud."] = ("После перевода результат попадает в буфер обмена и готов к вставке через Ctrl+V. Команда «Перевести и озвучить» также читает перевод вслух.", "Після перекладу результат потрапляє до буфера обміну й готовий до вставлення через Ctrl+V. Команда «Перекласти й озвучити» також читає переклад уголос."),
        ["Reliability: Edge and Google may change their unofficial web interfaces, so they are tried first for quality. Piper is a fully local fallback. After its one-time setup, Russian, Ukrainian, and English speech remain available without internet or if both online services fail."] = ("Надёжность: Edge и Google могут менять неофициальные веб-интерфейсы. Piper — полностью локальный резерв. После однократной настройки русский, украинский и английский доступны без интернета и при сбое онлайн-сервисов.", "Надійність: Edge і Google можуть змінювати неофіційні вебінтерфейси. Piper — повністю локальний резерв. Після одноразового налаштування російська, українська й англійська доступні без інтернету та в разі збою онлайн-сервісів."),
        ["ClipVoice does not record detailed events during normal use. Only errors, engine failures, and other faults are logged; speech text is not saved."] = ("При обычной работе ClipVoice не записывает подробные события. В журнал попадают только ошибки и сбои; текст озвучивания не сохраняется.", "Під час звичайної роботи ClipVoice не записує докладні події. До журналу потрапляють лише помилки та збої; текст озвучення не зберігається."),
        ["Open with a right-click shortcut"] = ("Открывать сочетанием с правой кнопкой мыши", "Відкривати сполученням із правою кнопкою миші"),
        ["Click and drag a folder or template to change its order. Drag a template to another folder on the left to move it. The order is saved and used in the context menu."] = ("Зажмите и перетащите папку или шаблон, чтобы изменить порядок. Перетащите шаблон в другую папку слева, чтобы переместить его. Порядок сохраняется и используется в контекстном меню.", "Затисніть і перетягніть теку або шаблон, щоб змінити порядок. Перетягніть шаблон до іншої теки ліворуч, щоб перемістити його. Порядок зберігається та використовується в контекстному меню."),
        ["Select a template to edit or delete • double-click to edit"] = ("Выберите шаблон для изменения или удаления • двойной клик — изменить", "Виберіть шаблон для редагування або видалення • подвійне клацання — редагувати"),
        ["A hotkey is optional: click the field on the right and press a shortcut, such as Ctrl+Alt+1. All templates remain available in the context menu."] = ("Горячая клавиша необязательна: нажмите на поле справа и задайте сочетание, например Ctrl+Alt+1. Все шаблоны доступны в контекстном меню.", "Гаряча клавіша необов’язкова: натисніть поле праворуч і задайте сполучення, наприклад Ctrl+Alt+1. Усі шаблони доступні в контекстному меню."),
        ["Could not insert template “"] = ("Не удалось вставить шаблон «", "Не вдалося вставити шаблон «"),
        ["Could not find a field for inserting template “"] = ("Не найдено поле для вставки шаблона «", "Не знайдено поле для вставлення шаблону «"),
        ["Created template “"] = ("Создан шаблон «", "Створено шаблон «"),
        ["History is limited to 25 items. History and templates are encrypted with Windows DPAPI and accessible only to the current Windows user. Hotkey settings contain no secrets and are stored separately."] = ("История ограничена 25 элементами. История и шаблоны шифруются Windows DPAPI и доступны только текущему пользователю. Настройки клавиш хранятся отдельно и не содержат секретов.", "Історію обмежено 25 елементами. Історія та шаблони шифруються Windows DPAPI й доступні лише поточному користувачу. Налаштування клавіш зберігаються окремо й не містять секретів."),
        ["The audio cache is limited to 500 MB; files older than 30 days are removed automatically."] = ("Аудиокэш ограничен 500 МБ; файлы старше 30 дней удаляются автоматически.", "Аудіокеш обмежено 500 МБ; файли старші за 30 днів видаляються автоматично."),
        ["When auto-capture is enabled, just select the text."] = ("При включённом автозахвате достаточно выделить текст.", "Якщо ввімкнено автозахоплення, достатньо виділити текст."),
        ["Prepare a local voice after first use of a language (not at startup)"] = ("Подготавливать локальный голос после первого использования языка (не при запуске)", "Готувати локальний голос після першого використання мови (не під час запуску)"),
        ["●  Edge online"] = ("●  Edge онлайн", "●  Edge онлайн"), ["●  Google fallback"] = ("●  Резерв Google", "●  Резерв Google"), ["●  Piper local"] = ("●  Piper локально", "●  Piper локально"),
        ["Alt + Right-click"] = ("Alt + ПКМ", "Alt + ПКМ"), ["Ctrl + Right-click"] = ("Ctrl + ПКМ", "Ctrl + ПКМ"), ["Shift + Right-click"] = ("Shift + ПКМ", "Shift + ПКМ"),
        ["Ctrl + Shift + Right-click"] = ("Ctrl + Shift + ПКМ", "Ctrl + Shift + ПКМ"), ["Ctrl + Alt + Right-click"] = ("Ctrl + Alt + ПКМ", "Ctrl + Alt + ПКМ"), ["Alt + Shift + Right-click"] = ("Alt + Shift + ПКМ", "Alt + Shift + ПКМ"),
        ["Add templates from a TXT file"] = ("Добавить шаблоны из TXT-файла", "Додати шаблони з TXT-файлу"), ["Save all templates to a TXT file"] = ("Сохранить все шаблоны в TXT-файл", "Зберегти всі шаблони у TXT-файл"),
        ["ClipVoice"] = ("ClipVoice", "ClipVoice"), ["🌐 + ▶"] = ("🌐 + ▶", "🌐 + ▶"), ["Speech engine status"] = ("Состояние движков озвучивания", "Стан рушіїв озвучення"),
        ["Create a template from selection"] = ("Создать шаблон из выделения", "Створити шаблон із виділеного"),
        ["The selected text has been captured. Name the template and choose a folder."] = ("Выделенный текст скопирован. Назовите шаблон и выберите папку.", "Виділений текст скопійовано. Назвіть шаблон і виберіть теку."),
        ["choose from the list"] = ("выберите из списка", "виберіть зі списку"), ["New folder name"] = ("Название новой папки", "Назва нової теки"),
        ["Templates are inserted directly into the active field. The system clipboard is not changed."] = ("Шаблоны вставляются напрямую в активное поле. Системный буфер обмена при этом не меняется.", "Шаблони вставляються безпосередньо в активне поле. Системний буфер обміну не змінюється."),
        ["1. Name"] = ("1. Название", "1. Назва"), ["For example: Work login, Wi-Fi password, Standard reply."] = ("Например: Рабочий логин, Пароль Wi-Fi, Стандартный ответ.", "Наприклад: Робочий логін, Пароль Wi-Fi, Стандартна відповідь."),
        ["2. Template text"] = ("2. Текст шаблона", "2. Текст шаблону"), ["3. Options"] = ("3. Дополнительно", "3. Додатково"),
        ["Hide content in lists (password/token)"] = ("Скрывать текст в списках (пароль/токен)", "Приховувати текст у списках (пароль/токен)"),
        ["Lists will show •••••••• instead of the template text"] = ("В списках вместо текста шаблона будет показано ••••••••", "У списках замість тексту шаблону буде показано ••••••••"),
        ["Hotkey:"] = ("Горячая клавиша:", "Гаряча клавіша:"), ["Click and press your shortcut"] = ("Нажмите и задайте сочетание клавиш", "Натисніть і задайте сполучення клавіш"), ["Backspace/Delete — clear"] = ("Backspace/Delete — очистить", "Backspace/Delete — очистити"),
        ["Close"] = ("Закрыть", "Закрити"), ["Save"] = ("Сохранить", "Зберегти"), ["Create from selected text"] = ("Создать из выделенного текста", "Створити з виділеного тексту"),
        ["A hotkey conflict was detected. Open Settings."] = ("Обнаружен конфликт горячих клавиш. Откройте настройки.", "Виявлено конфлікт гарячих клавіш. Відкрийте налаштування."),
        ["No errors have been recorded yet."] = ("Ошибок пока нет.", "Помилок поки немає."), ["The error log was copied."] = ("Журнал ошибок скопирован.", "Журнал помилок скопійовано."),
        ["Could not copy the logs: "] = ("Не удалось скопировать журнал: ", "Не вдалося скопіювати журнал: "),
        ["Could not open the audio cache folder: "] = ("Не удалось открыть папку аудиокэша: ", "Не вдалося відкрити теку аудіокешу: "),
        ["Checking Edge and Google, then running a real Piper offline test…"] = ("Проверка Edge и Google, затем офлайн-тест Piper…", "Перевірка Edge і Google, потім офлайн-тест Piper…"),
        ["Speech check completed."] = ("Проверка озвучивания завершена.", "Перевірку озвучення завершено."),
        ["Reading the same test phrase with Edge, Google, and Piper and measuring duration…"] = ("Проверяю одинаковую фразу в Edge, Google и Piper…", "Перевіряю однакову фразу в Edge, Google і Piper…"),
        ["Speed comparison completed."] = ("Сравнение скорости завершено.", "Порівняння швидкості завершено."),
        ["Preparing the offline fallback. The runtime and three voices must be downloaded once…"] = ("Подготовка локального резерва. Нужно один раз скачать движок и три голоса…", "Підготовка локального резерву. Потрібно один раз завантажити рушій і три голоси…"),
        ["Piper and its voices are installed. Click “Check all, including offline” to verify synthesis."] = ("Piper и голоса установлены. Нажмите «Проверить всё, включая офлайн», чтобы проверить синтез.", "Piper і голоси встановлено. Натисніть «Перевірити все, включно з офлайн», щоб перевірити синтез."),
        ["Translation error: "] = ("Ошибка перевода: ", "Помилка перекладу: "), ["Speech error: "] = ("Ошибка озвучивания: ", "Помилка озвучення: "),
        ["Translation or speech error: "] = ("Ошибка перевода или озвучивания: ", "Помилка перекладу або озвучення: "),
        ["Could not insert the template: "] = ("Не удалось вставить шаблон: ", "Не вдалося вставити шаблон: "),
        ["Could not copy the selected text. Check that some text is selected."] = ("Не удалось скопировать выделенный текст. Проверьте, что текст выделен.", "Не вдалося скопіювати виділений текст. Переконайтеся, що текст виділено."),
        ["Could not retrieve the selected text. Keep it selected and try again."] = ("Не удалось получить выделенный текст. Оставьте его выделенным и попробуйте снова.", "Не вдалося отримати виділений текст. Залиште його виділеним і спробуйте ще раз."),
        ["There is no text. Select some text or copy it to the clipboard."] = ("Нет текста. Выделите текст или скопируйте его в буфер обмена.", "Немає тексту. Виділіть текст або скопіюйте його до буфера обміну."),
        ["Could not find a field to capture the selected text from."] = ("Не найдено поле для получения выделенного текста.", "Не знайдено поле для отримання виділеного тексту."),
        ["Edge is unavailable — trying online speech fallback…"] = ("Edge недоступен — пробую резервное онлайн-озвучивание…", "Edge недоступний — пробую резервне онлайн-озвучення…"),
        ["Online speech is unavailable — switching to local Piper…"] = ("Онлайн-озвучивание недоступно — переключаюсь на локальный Piper…", "Онлайн-озвучення недоступне — перемикаюся на локальний Piper…"),
        ["Could not configure startup: "] = ("Не удалось настроить автозапуск: ", "Не вдалося налаштувати автозапуск: "), ["Could not save local data: "] = ("Не удалось сохранить локальные данные: ", "Не вдалося зберегти локальні дані: "),
        [" • translating…"] = (" • перевод…", " • переклад…"), [" • translated, reading aloud…"] = (" • перевод готов, озвучиваю…", " • переклад готовий, озвучую…"),
        [" • translation ready, generating speech…"] = (" • перевод готов, создаю аудио…", " • переклад готовий, створюю аудіо…"),
        [" • ready for Ctrl+V"] = (" • готово для Ctrl+V", " • готово для Ctrl+V"), [" • speaking • ready for Ctrl+V"] = (" • озвучивается • готово для Ctrl+V", " • озвучується • готово для Ctrl+V"),
        ["Foreign text"] = ("Иностранный текст", "Іноземний текст"),
    };

    public static string NormalizeLanguage(string? language)
        => language is "ru" or "uk" ? language : "en";

    public static string Translate(string? text, string? language)
    {
        if (text is null || NormalizeLanguage(language) == "en") return text ?? string.Empty;
        if (Texts.TryGetValue(text, out var value))
            return NormalizeLanguage(language) == "ru" ? value.Ru : value.Uk;

        var localized = text;
        foreach (var suffix in Texts.Keys.Where(key => key.StartsWith(" •", StringComparison.Ordinal) && localized.Contains(key, StringComparison.Ordinal)))
        {
            var translatedSuffix = NormalizeLanguage(language) == "ru" ? Texts[suffix].Ru : Texts[suffix].Uk;
            localized = localized.Replace(suffix, translatedSuffix, StringComparison.Ordinal);
        }
        var prefix = Texts.Keys.Where(key => key.Length > 0 && localized.StartsWith(key, StringComparison.Ordinal))
            .OrderByDescending(key => key.Length).FirstOrDefault();
        if (prefix is null) return localized;
        var translatedPrefix = NormalizeLanguage(language) == "ru" ? Texts[prefix].Ru : Texts[prefix].Uk;
        return translatedPrefix + localized[prefix.Length..];
    }

    public static void Apply(Window window, string? language)
        => ApplyNode(window, NormalizeLanguage(language));

    private static void ApplyNode(DependencyObject node, string language)
    {
        var original = Originals.GetValue(node, _ => new OriginalText());
        var isDynamic = node is FrameworkElement named && named.Name is ("StatusText" or "TtsStatusText" or "HotkeySummaryText" or "TemplateMouseHintText");
        if (node is TextBlock text && !isDynamic)
        {
            original.Text ??= text.Text;
            text.Text = Translate(original.Text, language);
        }
        if (node is ContentControl content && content.Content is string contentText && !isDynamic)
        {
            original.Content ??= contentText;
            content.Content = Translate(original.Content, language);
        }
        if (node is HeaderedContentControl headered && headered.Header is string headerText)
        {
            original.Header ??= headerText;
            headered.Header = Translate(original.Header, language);
        }
        if (node is FrameworkElement element)
        {
            if (element.ToolTip is string tip)
            {
                original.ToolTip ??= tip;
                element.ToolTip = Translate(original.ToolTip, language);
            }
            if (element is Window window)
            {
                original.Title ??= window.Title;
                window.Title = Translate(original.Title, language);
            }
        }
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            ApplyNode(child, language);
    }
}
