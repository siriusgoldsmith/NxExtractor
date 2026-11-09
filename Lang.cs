namespace NxExtractor;

/// <summary>Textos de la interfaz y del log. Orden de columnas: en, es, fr, ja, de.</summary>
public static class L
{
    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"), ("es", "Español"), ("fr", "Français"), ("ja", "日本語"), ("de", "Deutsch")
    };

    private static int _index;

    public static string Current
    {
        get => Languages[_index].Code;
        set
        {
            int i = Array.FindIndex(Languages, l => l.Code == value);
            _index = i < 0 ? 0 : i;
        }
    }

    public static string CurrentName => Languages[_index].Name;

    public static string T(string key) =>
        Strings.TryGetValue(key, out var s) ? s[_index] : key;

    public static string F(string key, params object[] args) => string.Format(T(key), args);

    private static readonly Dictionary<string, string[]> Strings = new()
    {
        // ---------- Interfaz ----------
        ["language"] = new[] { "Language", "Idioma", "Langue", "言語", "Sprache" },
        ["chooseLanguage"] = new[] { "Choose the interface language:", "Elige el idioma de la interfaz:", "Choisissez la langue de l’interface :", "表示言語を選択してください:", "Sprache der Oberfläche wählen:" },
        ["ok"] = new[] { "OK", "Aceptar", "OK", "OK", "OK" },
        ["cancel"] = new[] { "Cancel", "Cancelar", "Annuler", "キャンセル", "Abbrechen" },
        ["keys"] = new[] { "Keys", "Claves", "Clés", "キー", "Schlüssel" },
        ["titleKeysOpt"] = new[] { "title.keys (optional)", "title.keys (opcional)", "title.keys (facultatif)", "title.keys (任意)", "title.keys (optional)" },
        ["filterKeys"] = new[] { "Keys (*.keys)|*.keys|All files (*.*)|*.*", "Keys (*.keys)|*.keys|Todos (*.*)|*.*", "Clés (*.keys)|*.keys|Tous les fichiers (*.*)|*.*", "キー (*.keys)|*.keys|すべてのファイル (*.*)|*.*", "Schlüssel (*.keys)|*.keys|Alle Dateien (*.*)|*.*" },
        ["filterRoms"] = new[]
        {
            "Switch ROMs (*.nsp;*.nsz;*.xci;*.xcz)|*.nsp;*.nsz;*.xci;*.xcz|All files (*.*)|*.*",
            "Roms de Switch (*.nsp;*.nsz;*.xci;*.xcz)|*.nsp;*.nsz;*.xci;*.xcz|Todos (*.*)|*.*",
            "ROM Switch (*.nsp;*.nsz;*.xci;*.xcz)|*.nsp;*.nsz;*.xci;*.xcz|Tous les fichiers (*.*)|*.*",
            "Switch ROM (*.nsp;*.nsz;*.xci;*.xcz)|*.nsp;*.nsz;*.xci;*.xcz|すべてのファイル (*.*)|*.*",
            "Switch-ROMs (*.nsp;*.nsz;*.xci;*.xcz)|*.nsp;*.nsz;*.xci;*.xcz|Alle Dateien (*.*)|*.*"
        },
        ["tabExtract"] = new[] { "Extract", "Extraer", "Extraire", "展開", "Extrahieren" },
        ["tabConvert"] = new[] { "Convert", "Convertir", "Convertir", "変換", "Konvertieren" },
        ["game"] = new[] { "Game", "Juego", "Jeu", "ゲーム", "Spiel" },
        ["baseGame"] = new[] { "Base game", "Juego base", "Jeu de base", "ベースゲーム", "Basisspiel" },
        ["update"] = new[] { "Update", "Update", "Mise à jour", "アップデート", "Update" },
        ["dlc"] = new[] { "DLC", "DLC", "DLC", "DLC", "DLC" },
        ["add"] = new[] { "Add...", "Añadir...", "Ajouter...", "追加...", "Hinzufügen..." },
        ["remove"] = new[] { "Remove", "Quitar", "Retirer", "削除", "Entfernen" },
        ["clearList"] = new[] { "Clear", "Vaciar", "Vider", "クリア", "Leeren" },
        ["output"] = new[] { "Output", "Salida", "Sortie", "出力", "Ausgabe" },
        ["folder"] = new[] { "Folder", "Carpeta", "Dossier", "フォルダー", "Ordner" },
        ["nacpIcons"] = new[] { "control.nacp + icons", "control.nacp + iconos", "control.nacp + icônes", "control.nacp + アイコン", "control.nacp + Symbole" },
        ["extractBtn"] = new[] { "Extract", "Extraer", "Extraire", "展開", "Extrahieren" },
        ["filesGroup"] = new[] { "Files (NSP / NSZ / XCI / XCZ) — drag here", "Archivos (NSP / NSZ / XCI / XCZ) — arrastra aquí", "Fichiers (NSP / NSZ / XCI / XCZ) — glissez-les ici", "ファイル (NSP / NSZ / XCI / XCZ) — ここにドラッグ", "Dateien (NSP / NSZ / XCI / XCZ) — hierher ziehen" },
        ["toNsp"] = new[] { "to NSP (XCI → NSP, NSZ → NSP)", "a NSP (XCI → NSP, NSZ → NSP)", "en NSP (XCI → NSP, NSZ → NSP)", "NSP へ (XCI → NSP, NSZ → NSP)", "nach NSP (XCI → NSP, NSZ → NSP)" },
        ["toNsz"] = new[] { "to NSZ (NSP → NSZ, XCI → NSZ)", "a NSZ (NSP → NSZ, XCI → NSZ)", "en NSZ (NSP → NSZ, XCI → NSZ)", "NSZ へ (NSP → NSZ, XCI → NSZ)", "nach NSZ (NSP → NSZ, XCI → NSZ)" },
        ["zstdLevel"] = new[] { "zstd level:", "Nivel zstd:", "Niveau zstd :", "zstd レベル:", "zstd-Stufe:" },
        ["conversion"] = new[] { "Conversion", "Conversión", "Conversion", "変換設定", "Konvertierung" },
        ["outputFolder"] = new[] { "Output folder", "Carpeta de salida", "Dossier de sortie", "出力フォルダー", "Ausgabeordner" },
        ["convertBtn"] = new[] { "Convert", "Convertir", "Convertir", "変換", "Konvertieren" },

        // ---------- Mensajes ----------
        ["errProd"] = new[] { "Select a valid prod.keys file.", "Selecciona un prod.keys válido.", "Sélectionnez un fichier prod.keys valide.", "有効な prod.keys を選択してください。", "Wähle eine gültige prod.keys-Datei." },
        ["errTitle"] = new[] { "The title.keys file does not exist.", "El title.keys no existe.", "Le fichier title.keys n’existe pas.", "title.keys が見つかりません。", "Die title.keys-Datei existiert nicht." },
        ["errNoInput"] = new[] { "Add at least a game, update or DLC.", "Añade al menos un juego, update o DLC.", "Ajoutez au moins un jeu, une mise à jour ou un DLC.", "ゲーム、アップデート、DLC のいずれかを追加してください。", "Füge mindestens ein Spiel, Update oder DLC hinzu." },
        ["errMissing"] = new[] { "One of the input files does not exist.", "Algún archivo de entrada no existe.", "Un des fichiers d’entrée n’existe pas.", "存在しない入力ファイルがあります。", "Eine der Eingabedateien existiert nicht." },
        ["errOut"] = new[] { "Select an output folder.", "Selecciona una carpeta de salida.", "Sélectionnez un dossier de sortie.", "出力フォルダーを選択してください。", "Wähle einen Ausgabeordner." },
        ["errNoFiles"] = new[] { "Add at least one file.", "Añade al menos un archivo.", "Ajoutez au moins un fichier.", "ファイルを 1 つ以上追加してください。", "Füge mindestens eine Datei hinzu." },
        ["error"] = new[] { "Error", "Error", "Erreur", "エラー", "Fehler" },
        ["extractDone"] = new[] { "Extraction finished. Open the output folder?", "Extracción terminada. ¿Abrir la carpeta de salida?", "Extraction terminée. Ouvrir le dossier de sortie ?", "展開が完了しました。出力フォルダーを開きますか?", "Extraktion abgeschlossen. Ausgabeordner öffnen?" },
        ["convDoneLog"] = new[] { "Finished: {0}/{1} converted.", "Terminado: {0}/{1} convertidos.", "Terminé : {0}/{1} convertis.", "完了: {0}/{1} 件変換しました。", "Fertig: {0}/{1} konvertiert." },
        ["convDoneMsg"] = new[] { "{0} of {1} files converted. Open the output folder?", "{0} de {1} archivos convertidos. ¿Abrir la carpeta de salida?", "{0} fichier(s) sur {1} converti(s). Ouvrir le dossier de sortie ?", "{1} 件中 {0} 件を変換しました。出力フォルダーを開きますか?", "{0} von {1} Dateien konvertiert. Ausgabeordner öffnen?" },

        // ---------- Log de los motores ----------
        ["logOpening"] = new[] { "Opening [{0}] {1}", "Abriendo [{0}] {1}", "Ouverture [{0}] {1}", "開いています [{0}] {1}", "Öffne [{0}] {1}" },
        ["kindBase"] = new[] { "Base", "Base", "Base", "ベース", "Basis" },
        ["kindUpdate"] = new[] { "Update", "Update", "Mise à jour", "アップデート", "Update" },
        ["kindDlc"] = new[] { "DLC", "DLC", "DLC", "DLC", "DLC" },
        ["noSecure"] = new[] { "The XCI has no secure partition.", "El XCI no tiene partición segura.", "Le XCI n’a pas de partition sécurisée.", "XCI にセキュアパーティションがありません。", "Das XCI hat keine Secure-Partition." },
        ["logDecompressing"] = new[] { "  Decompressing {0}...", "  Descomprimiendo {0}...", "  Décompression de {0}...", "  {0} を解凍中...", "  Entpacke {0}..." },
        ["logNczDone"] = new[] { "    NCZ decompressed ({0} MiB), re-encrypting sections...", "    NCZ descomprimido ({0} MiB), re-cifrando secciones...", "    NCZ décompressé ({0} Mio), rechiffrement des sections...", "    NCZ を解凍しました ({0} MiB)。セクションを再暗号化中...", "    NCZ entpackt ({0} MiB), Abschnitte werden neu verschlüsselt..." },
        ["badNcz"] = new[] { "Invalid NCZ: NCZSECTN header missing", "NCZ inválido: falta NCZSECTN", "NCZ invalide : en-tête NCZSECTN manquant", "無効な NCZ: NCZSECTN ヘッダーがありません", "Ungültiges NCZ: NCZSECTN-Header fehlt" },
        ["logGame"] = new[] { "Game {0}", "Juego {0}", "Jeu {0}", "ゲーム {0}", "Spiel {0}" },
        ["logPlusUpdate"] = new[] { " + update", " + update", " + mise à jour", " + アップデート", " + Update" },
        ["logExtracting"] = new[] { "  Extracting {0} -> {1}", "  Extrayendo {0} -> {1}", "  Extraction de {0} -> {1}", "  {0} を展開中 -> {1}", "  Extrahiere {0} -> {1}" },
        ["romfsWithUpdate"] = new[] { "RomFS (update applied)", "RomFS (con update aplicado)", "RomFS (mise à jour appliquée)", "RomFS (アップデート適用済み)", "RomFS (Update angewendet)" },
        ["updateNoBase"] = new[]
        {
            "[!] Update {0} without base game: only ExeFS is extracted (RomFS needs the base game).",
            "[!] Update {0} sin juego base: sólo se extrae ExeFS (el RomFS necesita el base).",
            "[!] Mise à jour {0} sans jeu de base : seul l’ExeFS est extrait (le RomFS nécessite le jeu de base).",
            "[!] アップデート {0} にベースゲームがありません: ExeFS のみ展開します (RomFS にはベースゲームが必要です)。",
            "[!] Update {0} ohne Basisspiel: nur ExeFS wird extrahiert (RomFS benötigt das Basisspiel)."
        },
        ["noSection"] = new[] { "  [!] {0}: the NCA has no such section.", "  [!] {0}: la NCA no tiene esa sección.", "  [!] {0} : la NCA ne contient pas cette section.", "  [!] {0}: NCA にこのセクションがありません。", "  [!] {0}: Die NCA hat diesen Abschnitt nicht." },
        ["infoName"] = new[] { "Name", "Nombre", "Nom", "名前", "Name" },
        ["infoVersion"] = new[] { "Version", "Versión", "Version", "バージョン", "Version" },
        ["logDone"] = new[] { "Done.", "Listo.", "Terminé.", "完了。", "Fertig." },
        ["logCompressing"] = new[] { "  Compressing {0} (level {1})", "  Comprimiendo {0} (nivel {1})", "  Compression de {0} (niveau {1})", "  {0} を圧縮中 (レベル {1})", "  Komprimiere {0} (Stufe {1})" },
        ["logCopying"] = new[] { "  Copying {0}", "  Copiando {0}", "  Copie de {0}", "  {0} をコピー中", "  Kopiere {0}" },
        ["tooManyFiles"] = new[] { "Too many files for the PFS0 header.", "Demasiados archivos para la cabecera PFS0.", "Trop de fichiers pour l’en-tête PFS0.", "PFS0 ヘッダーに対してファイルが多すぎます。", "Zu viele Dateien für den PFS0-Header." },
        ["converted"] = new[] { "converted", "convertido", "converti", "変換済み", "konvertiert" },
    };
}
