// Kopiert das gebaute Plugin in den Plugins-Ordner von Stream Deck.
//
//     node tools/deploy.mjs
//
// Warum kopieren statt verknüpfen: `streamdeck link` legt eine Junction an.
// Zeigt sie auf ein Netzlaufwerk - dieses Projekt liegt auf einem - , kann
// Stream Deck sie nicht auflösen und meldet beim Start
// „The data present in the reparse point buffer is invalid".
//
// Folge: Nach jedem Bauen muss neu kopiert werden. Danach Stream Deck neu
// starten, damit es den Ordner einliest.

import { cpSync, existsSync, rmSync, statSync } from "node:fs";
import { join } from "node:path";

const UUID = "com.cydato.decksense";
const ORDNER = `${UUID}.sdPlugin`;

const quelle = join(import.meta.dirname, "..", ORDNER);

const appData = process.env.APPDATA;
if (!appData) {
	console.error("APPDATA ist nicht gesetzt - läuft das hier auf Windows?");
	process.exit(1);
}

const ziel = join(appData, "Elgato", "StreamDeck", "Plugins", ORDNER);

if (!existsSync(join(quelle, "bin", "plugin.js"))) {
	console.error(`Kein gebautes Plugin gefunden. Erst "npm run build" ausführen.`);
	process.exit(1);
}

// Eine vorhandene Junction muss weg, bevor kopiert wird - sonst landet die
// Kopie im Ziel der Junction, also wieder in der Quelle.
if (existsSync(ziel)) {
	const info = statSync(ziel, { throwIfNoEntry: false });
	const istVerknuepfung = info !== undefined && !info.isDirectory();

	try {
		rmSync(ziel, { recursive: true, force: true });
		console.log(istVerknuepfung ? "Vorhandene Verknüpfung entfernt." : "Vorherige Kopie entfernt.");
	} catch (fehler) {
		// Läuft das Plugin noch, hält Stream Deck die Dateien offen.
		console.error("Die vorherige Kopie ließ sich nicht entfernen - vermutlich läuft das Plugin noch.");
		console.error("");
		console.error("  npx @elgato/cli stop com.cydato.decksense");
		console.error("  npm run deploy");
		console.error("");
		console.error(`Ursprüngliche Meldung: ${fehler instanceof Error ? fehler.message : String(fehler)}`);
		process.exit(1);
	}
}

cpSync(quelle, ziel, { recursive: true });
console.log(`Kopiert nach ${ziel}`);
console.log("");
console.log("Stream Deck muss jetzt neu gestartet werden, damit es den Ordner einliest.");
