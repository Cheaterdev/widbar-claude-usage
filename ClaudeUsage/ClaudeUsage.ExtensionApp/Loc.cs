using System.Globalization;
using System.Text.Json;

namespace ClaudeUsage.ExtensionApp;

/// <summary>
/// Translations. English text is the key, so a missing entry or language simply
/// shows English. The language follows WidBar's own Language setting (config.json);
/// when that is "auto"/empty it follows the Windows display language.
/// </summary>
internal static class Loc
{
    // Order of the columns below.
    private static readonly string[] Langs = ["uk", "de", "es", "fr", "it"];

    private const string Blocked = "ПТН ПНХ";

    private static string _lang = "en";
    private static DateTime _checked = DateTime.MinValue;

    static Loc() => Refresh();

    public static string Lang => _lang;

    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentCulture;

    /// <summary>Re-reads the language (at most every 5 s). True when it changed.</summary>
    public static bool Refresh()
    {
        if (DateTime.UtcNow - _checked < TimeSpan.FromSeconds(5)) return false;
        _checked = DateTime.UtcNow;
        var old = _lang;
        var tag = FromWidBar();
        CultureInfo culture;
        try { culture = string.IsNullOrWhiteSpace(tag) ? CultureInfo.CurrentUICulture : new CultureInfo(tag); }
        catch (CultureNotFoundException) { culture = CultureInfo.CurrentUICulture; }
        _lang = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        // No Russian anywhere, not even in day or month names.
        Culture = _lang == "ru" ? CultureInfo.InvariantCulture : culture;
        return _lang != old;
    }

    // WidBar stores its language in %APPDATA%\WidBar\config.json inside its package.
    private static string? FromWidBar()
    {
        try
        {
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            foreach (var dir in Directory.EnumerateDirectories(packages, "*WidBar-WidgetTaskbarsystem*"))
            {
                var file = Path.Combine(dir, "LocalCache", "Roaming", "WidBar", "config.json");
                if (!File.Exists(file)) continue;
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs, new JsonDocumentOptions { AllowTrailingCommas = true });
                if (doc.RootElement.TryGetProperty("Language", out var l) && l.ValueKind == JsonValueKind.String)
                {
                    var s = l.GetString();
                    return string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase) ? null : s;
                }
            }
        }
        catch { /* fall back to the Windows language */ }
        return null;
    }

    public static string T(string key, params object[] args)
    {
        if (_lang == "ru") return Blocked; // Russian is deliberately not supported
        var text = key.EndsWith(" (short)", StringComparison.Ordinal) ? key[..^8] : key;
        var col = Array.IndexOf(Langs, _lang);
        if (col >= 0 && Table.TryGetValue(key, out var row) && !string.IsNullOrEmpty(row[col])) text = row[col];
        return args.Length == 0 ? text : string.Format(Culture, text, args);
    }

    private static readonly Dictionary<string, string[]> Table = Build();

    private static Dictionary<string, string[]> Build()
    {
        var d = new Dictionary<string, string[]>();
        // key, uk, de, es, fr, it
        void R(string key, string uk, string de, string es, string fr, string it) =>
            d[key] = [uk, de, es, fr, it];

        R("Claude usage", "Використання Claude", "Claude-Nutzung", "Uso de Claude", "Utilisation de Claude", "Utilizzo di Claude");
        R("Claude", "Claude", "Claude", "Claude", "Claude", "Claude");
        R("Usage", "Використання", "Nutzung", "Uso", "Usage", "Utilizzo");
        R("Reading usage…", "Читаю використання…", "Nutzung wird gelesen …", "Leyendo el uso…", "Lecture de l'utilisation…", "Lettura dell'utilizzo…");
        R("No Claude Code usage found in the last 30 days.\nLooked in: {0}",
            "Використання Claude Code за останні 30 днів не знайдено.\nШукали в: {0}",
            "Keine Claude-Code-Nutzung in den letzten 30 Tagen gefunden.\nGesucht in: {0}",
            "No se encontró uso de Claude Code en los últimos 30 días.\nBuscado en: {0}",
            "Aucune utilisation de Claude Code trouvée ces 30 derniers jours.\nRecherché dans : {0}",
            "Nessun utilizzo di Claude Code negli ultimi 30 giorni.\nCercato in: {0}");
        R("{0} left", "Ще {0}", "Noch {0}", "Quedan {0}", "Reste {0}", "Mancano {0}");
        R("Resets {0}", "Скид. {0}", "Zurücksetzung {0}", "Se restablece {0}", "Réinit. {0}", "Reset {0}");
        R("On pace", "У темпі", "Im Plan", "Al ritmo", "Dans le rythme", "In linea");
        R("{0}% ahead", "На {0}% попереду", "{0}% voraus", "{0}% por delante", "{0}% d'avance", "{0}% avanti");
        R("{0}% behind", "На {0}% позаду", "{0}% zurück", "{0}% por detrás", "{0}% de retard", "{0}% indietro");
        R("5-hour limit", "Ліміт 5 год", "5-Stunden-Limit", "Límite de 5 horas", "Limite de 5 heures", "Limite 5 ore");
        R("Weekly limit", "Ліміт тижня", "Wochenlimit", "Límite semanal", "Limite hebdomadaire", "Limite settimanale");
        R("5 hours", "5 годин", "5 Stunden", "5 horas", "5 heures", "5 ore");
        R("7 days", "7 днів", "7 Tage", "7 días", "7 jours", "7 giorni");
        R("30 days", "30 днів", "30 Tage", "30 días", "30 jours", "30 giorni");
        R("Today", "Сьогодні", "Heute", "Hoy", "Aujourd'hui", "Oggi");
        R("Estimated API cost", "Орієнтовна вартість API", "Geschätzte API-Kosten", "Costo estimado de la API", "Coût estimé de l'API", "Costo API stimato");
        R("{0} tokens", "{0} токенів", "{0} Tokens", "{0} tokens", "{0} jetons", "{0} token");
        R("Sign in to Claude to show your real plan limits. Until then the rings use the budgets from settings.",
            "Увійдіть у Claude, щоб бачити реальні ліміти плану. Доти кільця використовують бюджети з налаштувань.",
            "Melde dich bei Claude an, um deine echten Planlimits zu sehen. Bis dahin nutzen die Ringe die Budgets aus den Einstellungen.",
            "Inicia sesión en Claude para ver los límites reales de tu plan. Hasta entonces, los anillos usan los presupuestos de los ajustes.",
            "Connectez-vous à Claude pour afficher les limites réelles de votre forfait. D'ici là, les anneaux utilisent les budgets des paramètres.",
            "Accedi a Claude per vedere i limiti reali del tuo piano. Fino ad allora gli anelli usano i budget delle impostazioni.");
        R("Couldn't read plan limits ({0}). Showing local estimates.",
            "Не вдалося прочитати ліміти плану ({0}). Показано локальні оцінки.",
            "Planlimits konnten nicht gelesen werden ({0}). Lokale Schätzungen werden angezeigt.",
            "No se pudieron leer los límites del plan ({0}). Se muestran estimaciones locales.",
            "Impossible de lire les limites du forfait ({0}). Estimations locales affichées.",
            "Impossibile leggere i limiti del piano ({0}). Mostro stime locali.");
        R("Waiting for sign-in…", "Очікуємо вхід…", "Warte auf Anmeldung …", "Esperando el inicio de sesión…", "En attente de connexion…", "In attesa dell'accesso…");
        R("Sign in to Claude", "Увійти в Claude", "Bei Claude anmelden", "Iniciar sesión en Claude", "Se connecter à Claude", "Accedi a Claude");
        R("Claude Code (claude.exe) was not found; install it to sign in.",
            "Claude Code (claude.exe) не знайдено; встановіть його, щоб увійти.",
            "Claude Code (claude.exe) wurde nicht gefunden; installiere es, um dich anzumelden.",
            "No se encontró Claude Code (claude.exe); instálalo para iniciar sesión.",
            "Claude Code (claude.exe) est introuvable ; installez-le pour vous connecter.",
            "Claude Code (claude.exe) non trovato; installalo per accedere.");
        R("Last 7 days", "Останні 7 днів", "Letzte 7 Tage", "Últimos 7 días", "7 derniers jours", "Ultimi 7 giorni");
        R("By model · 30 days (est. cost)", "За моделями · 30 днів (орієнт. вартість)",
            "Nach Modell · 30 Tage (gesch. Kosten)", "Por modelo · 30 días (costo est.)", "Par modèle · 30 jours (coût est.)", "Per modello · 30 giorni (costo stim.)");
        R("Totals include cache reads.", "Підсумки включають читання кешу.",
            "Summen enthalten Cache-Lesezugriffe.", "Los totales incluyen lecturas de caché.", "Les totaux incluent les lectures du cache.", "I totali includono le letture della cache.");
        R("Cache reads not counted ({0} in 30 days).", "Читання кешу не враховано ({0} за 30 днів).",
            "Cache-Lesezugriffe nicht gezählt ({0} in 30 Tagen).", "Lecturas de caché no contadas ({0} en 30 días).", "Lectures du cache non comptées ({0} sur 30 jours).", "Letture della cache non conteggiate ({0} in 30 giorni).");
        R("{0} Costs = tokens x Anthropic's published API prices, not your plan bill. Source: Claude Code logs. Updated {1}.",
            "{0} Вартість = токени × опубліковані ціни API Anthropic, а не рахунок за ваш план. Джерело: журнали Claude Code. Оновлено {1}.",
            "{0} Kosten = Tokens × veröffentlichte API-Preise von Anthropic, nicht deine Planrechnung. Quelle: Claude-Code-Logs. Aktualisiert {1}.",
            "{0} Costos = tokens × precios publicados de la API de Anthropic, no la factura de tu plan. Fuente: registros de Claude Code. Actualizado {1}.",
            "{0} Coûts = jetons × tarifs API publiés par Anthropic, pas la facture de votre forfait. Source : journaux de Claude Code. Mis à jour {1}.",
            "{0} Costi = token × prezzi API pubblicati da Anthropic, non la fattura del tuo piano. Fonte: log di Claude Code. Aggiornato {1}.");

        // Compact time left (taskbar label, flyout).
        R("{0}d {1}h", "{0}д {1}г", "{0}T {1}h", "{0}d {1}h", "{0}j {1}h", "{0}g {1}h");
        R("{0}h {1}m", "{0}г {1}хв", "{0}h {1}m", "{0}h {1}min", "{0}h {1}min", "{0}h {1}min");
        R("{0}m", "{0}хв", "{0}m", "{0}min", "{0}min", "{0}min");
        R("now", "зараз", "jetzt", "ahora", "maintenant", "ora");

        // Short taskbar labels (about 27 px available).
        R("5h", "5г", "5h", "5h", "5h", "5h");
        R("Week", "Тиж", "Wo.", "Sem", "Sem", "Sett");
        R("Today (short)", "Сьог", "Heute", "Hoy", "Auj.", "Oggi");

        // ---- Settings ----
        R("Live preview", "Попередній перегляд", "Live-Vorschau", "Vista previa", "Aperçu en direct", "Anteprima dal vivo");
        R("The taskbar widget with the current settings.", "Віджет на панелі завдань із поточними налаштуваннями.",
            "Das Taskleisten-Widget mit den aktuellen Einstellungen.", "El widget de la barra de tareas con los ajustes actuales.", "Le widget de la barre des tâches avec les paramètres actuels.", "Il widget della barra delle applicazioni con le impostazioni attuali.");
        R("Width {0} px", "Ширина {0} пкс", "Breite {0} px", "Ancho {0} px", "Largeur {0} px", "Larghezza {0} px");
        R("On", "Увімк.", "An", "Sí", "Activé", "Sì");
        R("Off", "Вимк.", "Aus", "No", "Désactivé", "No");
        R("Plan limits", "Ліміти плану", "Planlimits", "Límites del plan", "Limites du forfait", "Limiti del piano");
        R("Sign in with Claude Code to show your real 5-hour and weekly limits.",
            "Увійдіть через Claude Code, щоб бачити реальні ліміти на 5 годин і на тиждень.",
            "Melde dich mit Claude Code an, um deine echten 5-Stunden- und Wochenlimits zu sehen.",
            "Inicia sesión con Claude Code para ver tus límites reales de 5 horas y semanales.",
            "Connectez-vous avec Claude Code pour voir vos vraies limites de 5 heures et hebdomadaires.",
            "Accedi con Claude Code per vedere i limiti reali di 5 ore e settimanali.");
        R("Claude account", "Обліковий запис Claude", "Claude-Konto", "Cuenta de Claude", "Compte Claude", "Account Claude");
        R("Signed in. Showing your real 5-hour and weekly limits.", "Ви увійшли. Показано реальні ліміти на 5 годин і на тиждень.",
            "Angemeldet. Deine echten 5-Stunden- und Wochenlimits werden angezeigt.", "Sesión iniciada. Se muestran tus límites reales de 5 horas y semanales.", "Connecté. Vos vraies limites de 5 heures et hebdomadaires sont affichées.", "Accesso eseguito. Mostro i limiti reali di 5 ore e settimanali.");
        R("Not signed in. Using the token budgets below.", "Не ввійшли. Використовуються бюджети токенів нижче.",
            "Nicht angemeldet. Es werden die Token-Budgets unten verwendet.", "Sin iniciar sesión. Se usan los presupuestos de tokens de abajo.", "Non connecté. Les budgets de jetons ci-dessous sont utilisés.", "Non hai effettuato l'accesso. Uso i budget di token qui sotto.");
        R("Couldn't read plan limits ({0}). Using the token budgets below.", "Не вдалося прочитати ліміти плану ({0}). Використовуються бюджети токенів нижче.",
            "Planlimits konnten nicht gelesen werden ({0}). Es werden die Token-Budgets unten verwendet.", "No se pudieron leer los límites del plan ({0}). Se usan los presupuestos de tokens de abajo.", "Impossible de lire les limites du forfait ({0}). Les budgets de jetons ci-dessous sont utilisés.", "Impossibile leggere i limiti del piano ({0}). Uso i budget di token qui sotto.");
        R("Checking…", "Перевірка…", "Prüfe …", "Comprobando…", "Vérification…", "Controllo…");
        R("Claude Code (claude.exe) was not found. Install it to sign in.", "Claude Code (claude.exe) не знайдено. Встановіть його, щоб увійти.",
            "Claude Code (claude.exe) wurde nicht gefunden. Installiere es, um dich anzumelden.", "No se encontró Claude Code (claude.exe). Instálalo para iniciar sesión.", "Claude Code (claude.exe) est introuvable. Installez-le pour vous connecter.", "Claude Code (claude.exe) non trovato. Installalo per accedere.");
        R("Sign in", "Увійти", "Anmelden", "Iniciar sesión", "Se connecter", "Accedi");
        R("Sign in again", "Увійти знову", "Erneut anmelden", "Iniciar sesión de nuevo", "Se reconnecter", "Accedi di nuovo");
        R("{0} plan", "план {0}", "{0}-Plan", "plan {0}", "forfait {0}", "piano {0}");
        R("Waiting for sign-in in your browser…", "Очікуємо вхід у браузері…", "Warte auf die Anmeldung im Browser …", "Esperando el inicio de sesión en el navegador…", "En attente de la connexion dans le navigateur…", "In attesa dell'accesso nel browser…");
        R("Taskbar view", "Панель завдань", "Taskleiste", "Barra de tareas", "Barre des tâches", "Barra delle applicazioni");
        R("What the widget shows on the taskbar.", "Що віджет показує на панелі завдань.",
            "Was das Widget in der Taskleiste anzeigt.", "Qué muestra el widget en la barra de tareas.", "Ce que le widget affiche dans la barre des tâches.", "Cosa mostra il widget nella barra delle applicazioni.");
        R("Show the rolling 5-hour usage gauge.", "Показувати індикатор використання за ковзні 5 годин.",
            "Die gleitende 5-Stunden-Anzeige zeigen.", "Mostrar el indicador de uso de las últimas 5 horas.", "Afficher la jauge d'utilisation sur 5 heures glissantes.", "Mostra l'indicatore di utilizzo delle ultime 5 ore.");
        R("Show the 7-day usage gauge.", "Показувати індикатор використання за 7 днів.",
            "Die 7-Tage-Anzeige zeigen.", "Mostrar el indicador de uso de 7 días.", "Afficher la jauge d'utilisation sur 7 jours.", "Mostra l'indicatore di utilizzo di 7 giorni.");
        R("Daily usage", "Використання за день", "Tagesnutzung", "Uso diario", "Utilisation quotidienne", "Utilizzo giornaliero");
        R("Show today's gauge: your use against today's share of the weekly limit (the daily budget below when not signed in).", "Показувати індикатор за сьогодні: ваше використання відносно сьогоднішньої частки тижневого ліміту (без входу — денний бюджет нижче).", "Die Anzeige für heute zeigen: deine Nutzung im Verhältnis zum heutigen Anteil am Wochenlimit (ohne Anmeldung das Tagesbudget unten).", "Mostrar el indicador de hoy: tu uso frente a la parte de hoy del límite semanal (sin sesión, el presupuesto diario de abajo).", "Afficher la jauge du jour : votre utilisation par rapport à la part du jour de la limite hebdomadaire (sans connexion, le budget quotidien ci-dessous).", "Mostra l'indicatore di oggi: il tuo uso rispetto alla quota di oggi del limite settimanale (senza accesso, il budget giornaliero qui sotto).");
        R("Labels", "Підписи", "Beschriftungen", "Etiquetas", "Étiquettes", "Etichette");
        R("Show the small names (5h, Week, Today) above the percentages.", "Показувати малі назви (5г, Тиж, Сьог) над відсотками.",
            "Die kleinen Namen (5h, Wo., Heute) über den Prozentwerten zeigen.", "Mostrar los nombres pequeños (5h, Sem, Hoy) sobre los porcentajes.", "Afficher les petits noms (5h, Sem, Auj.) au-dessus des pourcentages.", "Mostra i nomi piccoli (5h, Sett, Oggi) sopra le percentuali.");
        R("Time left", "Залишок часу", "Verbleibende Zeit", "Tiempo restante", "Temps restant", "Tempo rimasto");
        R("Replace the 5h and Week labels with the time until each limit resets (needs sign-in).", "Замінити підписи 5г і Тиж часом до скидання кожного ліміту (потрібен вхід).",
            "Die Beschriftungen 5h und Wo. durch die Zeit bis zur Zurücksetzung jedes Limits ersetzen (Anmeldung nötig).", "Reemplazar las etiquetas 5h y Sem por el tiempo hasta que se restablezca cada límite (requiere iniciar sesión).", "Remplacer les étiquettes 5h et Sem par le temps avant la réinitialisation de chaque limite (connexion requise).", "Sostituisci le etichette 5h e Sett con il tempo al reset di ogni limite (richiede l'accesso).");
        R("Flyout", "Велике вікно", "Flyout", "Ventana grande", "Fenêtre détaillée", "Finestra grande");
        R("What the big window shows under each limit (needs sign-in).", "Що велике вікно показує під кожним лімітом (потрібен вхід).",
            "Was das große Fenster unter jedem Limit zeigt (Anmeldung nötig).", "Qué muestra la ventana grande bajo cada límite (requiere iniciar sesión).", "Ce que la fenêtre détaillée affiche sous chaque limite (connexion requise).", "Cosa mostra la finestra grande sotto ogni limite (richiede l'accesso).");
        R("Show the time left, the reset time and a progress bar for the window.", "Показувати залишок часу, час скидання та смужку прогресу вікна.",
            "Verbleibende Zeit, Zurücksetzungszeit und einen Fortschrittsbalken zeigen.", "Mostrar el tiempo restante, la hora de restablecimiento y una barra de progreso.", "Afficher le temps restant, l'heure de réinitialisation et une barre de progression.", "Mostra il tempo rimasto, l'ora del reset e una barra di avanzamento.");
        R("Pace", "Темп", "Tempo", "Ritmo", "Rythme", "Ritmo");
        R("Show whether you are ahead of, on, or behind pace for the window.", "Показувати, чи ви випереджаєте темп, у темпі чи відстаєте в межах вікна.",
            "Zeigen, ob du dem Plan voraus bist, im Plan liegst oder zurückliegst.", "Mostrar si vas por delante, al ritmo o por detrás en la ventana.", "Indiquer si vous êtes en avance, dans le rythme ou en retard sur la fenêtre.", "Mostra se sei avanti, in linea o indietro rispetto alla finestra.");
        R("{0}% of weekly limit", "{0}% від тижня", "{0}% des Wochenlimits", "{0}% del límite semanal", "{0}% de la limite hebdo", "{0}% del limite settimanale");
        R("Allowance {0}% / day", "Норма {0}%/день", "Rate {0}% / Tag", "Cupo {0}% / día", "Quota {0}% / jour", "Quota {0}% / giorno");
        R("Extra usage credits", "Додаткові кредити", "Zusätzliche Nutzungsguthaben", "Créditos de uso extra", "Crédits d'utilisation supplémentaires", "Crediti di utilizzo extra");
        R("of {0} · used {1}", "із {0} · використано {1}", "von {0} · verbraucht {1}", "de {0} · usado {1}", "sur {0} · utilisé {1}", "di {0} · usati {1}");
        R("Colours", "Кольори", "Farben", "Colores", "Couleurs", "Colori");
        R("Gauges turn amber, then red, as usage climbs.", "Індикатори стають бурштиновими, потім червоними, коли використання зростає.",
            "Die Anzeigen werden bernsteinfarben, dann rot, wenn die Nutzung steigt.", "Los indicadores pasan a ámbar y luego a rojo al subir el uso.", "Les jauges passent à l'orange puis au rouge à mesure que l'utilisation augmente.", "Gli indicatori diventano ambra e poi rossi con l'aumentare dell'utilizzo.");
        R("Warning at", "Попередження при", "Warnung bei", "Aviso al", "Avertissement à", "Avviso al");
        R("Percent used when a gauge turns amber.", "Відсоток використання, при якому індикатор стає бурштиновим.",
            "Prozent Nutzung, ab denen eine Anzeige bernsteinfarben wird.", "Porcentaje de uso al que un indicador pasa a ámbar.", "Pourcentage d'utilisation à partir duquel une jauge passe à l'orange.", "Percentuale di utilizzo a cui un indicatore diventa ambra.");
        R("Critical at", "Критично при", "Kritisch bei", "Crítico al", "Critique à", "Critico al");
        R("Percent used when a gauge turns red.", "Відсоток використання, при якому індикатор стає червоним.",
            "Prozent Nutzung, ab denen eine Anzeige rot wird.", "Porcentaje de uso al que un indicador pasa a rojo.", "Pourcentage d'utilisation à partir duquel une jauge passe au rouge.", "Percentuale di utilizzo a cui un indicatore diventa rosso.");
        R("Data", "Дані", "Daten", "Datos", "Données", "Dati");
        R("Where the numbers come from and how often they refresh.", "Звідки беруться числа і як часто вони оновлюються.",
            "Woher die Zahlen stammen und wie oft sie aktualisiert werden.", "De dónde vienen los números y con qué frecuencia se actualizan.", "D'où viennent les chiffres et à quelle fréquence ils sont actualisés.", "Da dove arrivano i numeri e ogni quanto si aggiornano.");
        R("1 minute", "1 хвилина", "1 Minute", "1 minuto", "1 minute", "1 minuto");
        R("{0} minutes", "{0} хвилин", "{0} Minuten", "{0} minutos", "{0} minutes", "{0} minuti");
        R("Refresh every", "Оновлювати кожні", "Aktualisieren alle", "Actualizar cada", "Actualiser toutes les", "Aggiorna ogni");
        R("Plan limits are asked for at most every 5 minutes.", "Ліміти плану запитуються не частіше ніж раз на 5 хвилин.",
            "Planlimits werden höchstens alle 5 Minuten abgefragt.", "Los límites del plan se consultan como máximo cada 5 minutos.", "Les limites du forfait sont demandées au plus toutes les 5 minutes.", "I limiti del piano vengono richiesti al massimo ogni 5 minuti.");
        R("Count cache reads", "Враховувати читання кешу", "Cache-Lesezugriffe zählen", "Contar lecturas de caché", "Compter les lectures du cache", "Conta le letture della cache");
        R("Include cached input in token totals. Cache reads are cheap, so they are off by default.", "Включати кешований ввід у підсумки токенів. Читання кешу дешеве, тому за замовчуванням вимкнено.",
            "Zwischengespeicherte Eingaben in die Token-Summen einbeziehen. Cache-Lesezugriffe sind günstig, daher standardmäßig aus.", "Incluir la entrada en caché en los totales de tokens. Las lecturas de caché son baratas, por eso está desactivado por defecto.", "Inclure l'entrée en cache dans les totaux de jetons. Les lectures du cache sont peu coûteuses, donc désactivé par défaut.", "Includi l'input in cache nei totali dei token. Le letture della cache costano poco, quindi è disattivato di default.");
        R("Claude data folder", "Папка даних Claude", "Claude-Datenordner", "Carpeta de datos de Claude", "Dossier de données Claude", "Cartella dati di Claude");
        R("Claude Code's session logs. Leave empty for the default.", "Журнали сесій Claude Code. Залиште порожнім для значення за замовчуванням.",
            "Sitzungsprotokolle von Claude Code. Leer lassen für den Standard.", "Registros de sesión de Claude Code. Déjalo vacío para el valor predeterminado.", "Journaux de session de Claude Code. Laissez vide pour la valeur par défaut.", "Log di sessione di Claude Code. Lascia vuoto per il valore predefinito.");
        R("Browse…", "Огляд…", "Durchsuchen…", "Examinar…", "Parcourir…", "Sfoglia…");
        R("All computers", "Усі комп'ютери", "Alle Computer", "Todos los equipos", "Tous les ordinateurs", "Tutti i computer");
        R("{0} (this PC)", "{0} (цей ПК)", "{0} (dieser PC)", "{0} (este equipo)", "{0} (cet ordinateur)", "{0} (questo PC)");
        R("Shared folder", "Спільна папка", "Gemeinsamer Ordner", "Carpeta compartida", "Dossier partagé", "Cartella condivisa");
        R("Not shared", "Не використовується", "Nicht geteilt", "No compartida", "Non partagé", "Non condivisa");
        R("Combine usage from several computers. Point each one at the same synced folder.",
            "Об'єднайте використання з кількох комп'ютерів. Вкажіть на всіх ту саму синхронізовану папку.",
            "Nutzung mehrerer Computer zusammenführen. Auf allen denselben synchronisierten Ordner angeben.",
            "Combina el uso de varios equipos. Indica en todos la misma carpeta sincronizada.",
            "Regroupe l'utilisation de plusieurs ordinateurs. Indiquez le même dossier synchronisé partout.",
            "Unisci l'uso di più computer. Indica la stessa cartella sincronizzata su ciascuno.");
        R("Token budgets", "Бюджети токенів", "Token-Budgets", "Presupuestos de tokens", "Budgets de jetons", "Budget di token");
        R("Used for the gauges when you are not signed in.", "Використовуються для індикаторів, коли ви не ввійшли.", "Wird für die Anzeigen ohne Anmeldung verwendet.", "Se usan para los indicadores sin sesión iniciada.", "Utilisés pour les jauges sans connexion.", "Usati per gli indicatori senza accesso.");
        R("5-hour budget", "Бюджет на 5 годин", "5-Stunden-Budget", "Presupuesto de 5 horas", "Budget de 5 heures", "Budget di 5 ore");
        R("Weekly budget", "Тижневий бюджет", "Wochenbudget", "Presupuesto semanal", "Budget hebdomadaire", "Budget settimanale");
        R("Daily budget", "Денний бюджет", "Tagesbudget", "Presupuesto diario", "Budget quotidien", "Budget giornaliero");
        R("Million tokens.", "Мільйонів токенів.", "Millionen Tokens.", "Millones de tokens.", "Millions de jetons.", "Milioni di token.");
        R("Million tokens, for the Today ring when not signed in.", "Мільйонів токенів, для кільця «Сьогодні» без входу.", "Millionen Tokens, für den Heute-Ring ohne Anmeldung.", "Millones de tokens, para el anillo de Hoy sin sesión.", "Millions de jetons, pour l'anneau Aujourd'hui sans connexion.", "Milioni di token, per l'anello Oggi senza accesso.");
        return d;
    }
}
