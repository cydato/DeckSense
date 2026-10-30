using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace DeckSense.Setup;

/// <summary>
/// Legt die geplante Aufgabe für den Autostart an oder entfernt sie.
///
/// Umgesetzt über <c>schtasks.exe</c> mit einer XML-Beschreibung, nicht über die
/// COM-Schnittstelle der Aufgabenplanung. Grund ist Nachvollziehbarkeit: Die XML
/// lässt sich im Fenster anzeigen und prüfen, bevor irgendetwas angelegt wird.
/// Bei einem Werkzeug, das SMART-Daten liest und erhöhte Rechte verlangt, ist
/// das mehr wert als ein paar Zeilen weniger Code.
/// </summary>
internal static class TaskSetup
{
    internal const string AufgabenName = "DeckSense Sensor Service";

    /// <summary>
    /// Der deutsche Name aus den Versionen bis 0.15.1.
    ///
    /// <para>
    /// Beim Umstellen der Oberflaeche auf Englisch in 0.16.0 wurde die Aufgabe
    /// umbenannt. Wer vorher eingerichtet hat, hat die alte noch liegen - und
    /// die startet den Dienst weiter. Deshalb wird sie mitgesucht und
    /// mitentfernt, statt still neben der neuen zu bleiben und den Dienst
    /// doppelt zu starten. Siehe Entscheidung 0028.
    /// </para>
    /// </summary>
    private const string AufgabenNameAlt = "DeckSense Sensordienst";

    /// <summary>
    /// Baut die XML-Beschreibung der Aufgabe.
    ///
    /// Die Eckpunkte stammen aus der Projektvorgabe: beim Anmelden des
    /// Benutzers, mit höchsten Privilegien, <b>in der Benutzersitzung</b> und
    /// ohne sichtbares Fenster. Session 0 wäre falsch - für die RTSS-Zuordnung
    /// muss das Vordergrundfenster lesbar sein.
    /// </summary>
    internal static string BuildXml(string exePfad)
    {
        string benutzer = WindowsIdentity.GetCurrent().Name;
        string arbeitsverzeichnis = Path.GetDirectoryName(exePfad) ?? string.Empty;

        var xml = new StringBuilder();
        xml.AppendLine(@"<?xml version=""1.0"" encoding=""UTF-16""?>");
        xml.AppendLine(@"<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">");
        xml.AppendLine("  <RegistrationInfo>");
        xml.AppendLine("    <Description>Starts the DeckSense sensor service at logon. Reads hardware values and serves them to the Stream Deck plugin on 127.0.0.1.</Description>");
        xml.AppendLine("  </RegistrationInfo>");
        xml.AppendLine("  <Triggers>");
        xml.AppendLine("    <LogonTrigger>");
        xml.AppendLine("      <Enabled>true</Enabled>");
        xml.AppendLine($"      <UserId>{Escape(benutzer)}</UserId>");
        xml.AppendLine("    </LogonTrigger>");
        xml.AppendLine("  </Triggers>");
        xml.AppendLine("  <Principals>");
        xml.AppendLine(@"    <Principal id=""Author"">");
        xml.AppendLine($"      <UserId>{Escape(benutzer)}</UserId>");
        // InteractiveToken: läuft in der Benutzersitzung, nicht in Session 0.
        xml.AppendLine("      <LogonType>InteractiveToken</LogonType>");
        xml.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
        xml.AppendLine("    </Principal>");
        xml.AppendLine("  </Principals>");
        xml.AppendLine("  <Settings>");
        xml.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        // Der Dienst soll auch im Akkubetrieb laufen - er ist der Grund, warum
        // die Tasten überhaupt Werte zeigen.
        xml.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        xml.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        xml.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
        xml.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
        xml.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
        xml.AppendLine("    <IdleSettings>");
        xml.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
        xml.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
        xml.AppendLine("    </IdleSettings>");
        xml.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
        xml.AppendLine("    <Enabled>true</Enabled>");
        xml.AppendLine("    <Hidden>false</Hidden>");
        xml.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
        // Ein Dauerläufer darf nach drei Tagen nicht abgeschossen werden.
        xml.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
        xml.AppendLine("    <Priority>7</Priority>");
        xml.AppendLine("    <RestartOnFailure>");
        xml.AppendLine("      <Interval>PT1M</Interval>");
        xml.AppendLine("      <Count>3</Count>");
        xml.AppendLine("    </RestartOnFailure>");
        xml.AppendLine("  </Settings>");
        xml.AppendLine(@"  <Actions Context=""Author"">");
        xml.AppendLine("    <Exec>");
        xml.AppendLine($"      <Command>{Escape(exePfad)}</Command>");
        xml.AppendLine($"      <WorkingDirectory>{Escape(arbeitsverzeichnis)}</WorkingDirectory>");
        xml.AppendLine("    </Exec>");
        xml.AppendLine("  </Actions>");
        xml.AppendLine("</Task>");

        return xml.ToString();
    }

    /// <summary>
    /// Ist eine Aufgabe eingerichtet? Der alte deutsche Name zaehlt mit, sonst
    /// meldete die Oberflaeche nach dem Umbenennen "not set up", obwohl der
    /// Dienst beim Anmelden weiter startet.
    /// </summary>
    internal static bool IstEingerichtet()
        => Vorhanden(AufgabenName) || Vorhanden(AufgabenNameAlt);

    /// <summary>Gibt es noch die Aufgabe unter dem alten Namen?</summary>
    internal static bool AlteAufgabeVorhanden() => Vorhanden(AufgabenNameAlt);

    private static bool Vorhanden(string name)
    {
        (int code, _) = Schtasks($"/query /tn \"{name}\"");
        return code == 0;
    }

    /// <summary>Legt die Aufgabe an. Verlangt erhöhte Rechte.</summary>
    internal static (bool Erfolg, string Meldung) Einrichten(string exePfad)
    {
        // UTF-16, weil die Aufgabenplanung das in der XML-Deklaration erwartet.
        string tempDatei = Path.Combine(Path.GetTempPath(), $"decksense-task-{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(tempDatei, BuildXml(exePfad), Encoding.Unicode);
            (int code, string ausgabe) = Schtasks($"/create /tn \"{AufgabenName}\" /xml \"{tempDatei}\" /f");

            if (code != 0)
            {
                return (false, ausgabe);
            }

            // Erst wenn die neue steht, die alte weg. Andersherum stuende man
            // bei einem Fehlschlag ganz ohne Autostart da.
            if (Vorhanden(AufgabenNameAlt))
            {
                (int alt, _) = Schtasks($"/delete /tn \"{AufgabenNameAlt}\" /f");
                ausgabe += alt == 0
                    ? $" Die aeltere Aufgabe \"{AufgabenNameAlt}\" wurde entfernt."
                    : $" Achtung: Die aeltere Aufgabe \"{AufgabenNameAlt}\" liegt noch und startet den Dienst ebenfalls.";
            }

            return (true, ausgabe);
        }
        catch (Exception fehler)
        {
            return (false, fehler.Message);
        }
        finally
        {
            try
            {
                File.Delete(tempDatei);
            }
            catch (Exception)
            {
                // Eine liegen gebliebene Temporärdatei ist kein Drama.
            }
        }
    }

    /// <summary>Entfernt die Aufgabe. Verlangt erhöhte Rechte.</summary>
    internal static (bool Erfolg, string Meldung) Entfernen()
    {
        (int code, string ausgabe) = Schtasks($"/delete /tn \"{AufgabenName}\" /f");

        // Auch die alte mitnehmen, sonst bleibt ein Autostart zurueck, den der
        // Knopf gerade zu entfernen versprochen hat.
        if (Vorhanden(AufgabenNameAlt))
        {
            (int alt, string ausgabeAlt) = Schtasks($"/delete /tn \"{AufgabenNameAlt}\" /f");
            ausgabe += ausgabeAlt;

            // Wenn nur die alte da war, gilt das als Erfolg.
            if (alt == 0)
            {
                return (true, ausgabe);
            }
        }

        return (code == 0, ausgabe);
    }

    private static (int Code, string Ausgabe) Schtasks(string argumente)
    {
        var start = new ProcessStartInfo("schtasks.exe", argumente)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            using Process? prozess = Process.Start(start);
            if (prozess is null)
            {
                return (-1, "schtasks.exe could not be started.");
            }

            string ausgabe = prozess.StandardOutput.ReadToEnd() + prozess.StandardError.ReadToEnd();
            prozess.WaitForExit(20_000);
            return (prozess.ExitCode, ausgabe.Trim());
        }
        catch (Exception fehler)
        {
            return (-1, fehler.Message);
        }
    }

    private static string Escape(string wert)
        => wert.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
