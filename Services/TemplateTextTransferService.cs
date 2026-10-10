using System.Text;
using System.Text.Json;
using ClipVoice.Models;

namespace ClipVoice.Services;

public static class TemplateTextTransferService
{
    private const string Header = "CLIPVOICE-TEMPLATES\t1";
    private const string FolderPrefix = "FOLDER\t";
    private const string TemplatePrefix = "TEMPLATE\t";
    private const string EndTemplate = "ENDTEMPLATE";

    public static string Export(IEnumerable<TemplateFolder> folders)
    {
        var output = new StringBuilder();
        output.AppendLine(Header);
        foreach (var folder in folders)
        {
            output.Append(FolderPrefix).AppendLine(JsonSerializer.Serialize(folder.Name));
            foreach (var template in folder.Templates)
            {
                output.Append(TemplatePrefix)
                    .Append(JsonSerializer.Serialize(template.Name)).Append('\t')
                    .Append(JsonSerializer.Serialize(template.Hotkey)).Append('\t')
                    .Append(template.PasteOnHotkey ? "true" : "false").Append('\t')
                    .AppendLine(template.IsSecret ? "true" : "false");
                output.AppendLine("CONTENT");

                var contentLines = (template.Content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                if (string.IsNullOrEmpty(template.Content)) contentLines = Array.Empty<string>();
                foreach (var line in contentLines)
                {
                    if (line.StartsWith('\\') || line == EndTemplate || line.StartsWith(FolderPrefix, StringComparison.Ordinal) || line.StartsWith(TemplatePrefix, StringComparison.Ordinal))
                        output.Append('\\');
                    output.AppendLine(line);
                }
                output.AppendLine(EndTemplate);
            }
        }
        return output.ToString();
    }

    public static List<TemplateFolder> Import(string text)
    {
        using var reader = new StringReader(text);
        if (!string.Equals(reader.ReadLine(), Header, StringComparison.Ordinal))
            throw new InvalidDataException("Это не TXT-файл шаблонов ClipVoice или его формат не поддерживается.");

        var folders = new List<TemplateFolder>();
        TemplateFolder? currentFolder = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith(FolderPrefix, StringComparison.Ordinal))
            {
                var name = ReadJsonString(line[FolderPrefix.Length..]);
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("В файле есть папка без названия.");
                currentFolder = new TemplateFolder { Name = name };
                folders.Add(currentFolder);
                continue;
            }

            if (!line.StartsWith(TemplatePrefix, StringComparison.Ordinal) || currentFolder is null)
                throw new InvalidDataException("Неожиданная строка в TXT-файле шаблонов.");

            var fields = line.Split('\t');
            if (fields.Length != 5 || fields[0] != "TEMPLATE"
                || !bool.TryParse(fields[3], out var pasteOnHotkey)
                || !bool.TryParse(fields[4], out var isSecret))
                throw new InvalidDataException("Не удалось прочитать свойства шаблона из TXT-файла.");

            var nameValue = ReadJsonString(fields[1]);
            var hotkeyValue = ReadJsonString(fields[2]);
            if (string.IsNullOrWhiteSpace(nameValue)) throw new InvalidDataException("В файле есть шаблон без названия.");
            if (reader.ReadLine() != "CONTENT") throw new InvalidDataException("В TXT-файле отсутствует содержимое шаблона.");

            var content = new List<string>();
            var ended = false;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line == EndTemplate) { ended = true; break; }
                content.Add(line.StartsWith('\\') ? line[1..] : line);
            }
            if (!ended) throw new InvalidDataException("TXT-файл оборван: не найден конец шаблона.");

            currentFolder.Templates.Add(new TextTemplate
            {
                Name = nameValue,
                Hotkey = hotkeyValue,
                PasteOnHotkey = pasteOnHotkey,
                IsSecret = isSecret,
                Content = string.Join("\n", content)
            });
        }

        if (folders.Count == 0) throw new InvalidDataException("В TXT-файле нет папок для импорта.");
        return folders;
    }

    private static string ReadJsonString(string value) => JsonSerializer.Deserialize<string>(value)
        ?? throw new InvalidDataException("Не удалось прочитать текстовое поле в TXT-файле.");
}
