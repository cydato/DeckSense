/**
 * Zeichnet das Tastenbild als SVG, 144 × 144.
 *
 * Farben nach Variante C aus `docs/design/tastenentwuerfe.html`: Violett trägt
 * Titel und Graph, der Wert bleibt weiß und wechselt nur dann die Farbe, wenn
 * eine Schwelle überschritten ist. Begründung samt Kontrastwerten steht dort.
 */

export const FARBEN = {
	grund: "#000000",
	titel: "#a78bfa",
	wertNormal: "#ffffff",
	wertWarnung: "#ffb01f",
	wertKritisch: "#ff4d4d",
	wertFehlt: "#6b6b78",
	graph: "#7800ff",
} as const;

export type Zustand = "normal" | "warnung" | "kritisch" | "fehlt";

export type GraphStil = "linie" | "balken";

export type Tastenbild = {
	/** Titelzeile, wird bei Bedarf auf zwei Zeilen umgebrochen. */
	readonly titel: string;
	/** Fertig formatierter Wert samt Einheit, etwa „47 °C" oder „--". */
	readonly wert: string;
	readonly zustand: Zustand;
	/** Verlaufswerte, älteste zuerst. `null` erzeugt eine Lücke. */
	readonly verlauf?: readonly (number | null)[];
	readonly graphStil?: GraphStil;
	readonly graphFarbe?: string;
	/**
	 * Kleinste Spanne, über die der Graph skaliert wird. Verhindert, dass ein
	 * ruhiger Wert wie ein Seismograf aussieht.
	 */
	readonly graphMindestspanne?: number;
};

const GROESSE = 144;
const SCHRIFT = "'Segoe UI', 'Liberation Sans', sans-serif";

/** Ab dieser Zeichenzahl wird der Titel zweizeilig gesetzt. */
const TITEL_UMBRUCH_AB = 11;

// Zeichenfläche des Graphen im unteren Drittel.
const GRAPH_LINKS = 10;
const GRAPH_RECHTS = 134;
const GRAPH_OBEN = 102;
const GRAPH_UNTEN = 134;

function wertFarbe(zustand: Zustand): string {
	switch (zustand) {
		case "warnung":
			return FARBEN.wertWarnung;
		case "kritisch":
			return FARBEN.wertKritisch;
		case "fehlt":
			return FARBEN.wertFehlt;
		default:
			return FARBEN.wertNormal;
	}
}

/**
 * Die Schriftgröße des Werts wächst mit, wie im bisherigen Aufbau des Nutzers:
 * „15249 MB" steht kleiner als „2%".
 */
function wertGroesse(wert: string): number {
	const laenge = wert.length;
	if (laenge <= 4) {
		return 40;
	}
	if (laenge <= 6) {
		return 34;
	}
	if (laenge <= 8) {
		return 28;
	}
	return 23;
}

/**
 * Bricht den Titel auf höchstens zwei Zeilen um, bevorzugt an einem
 * Leerzeichen nahe der Mitte. „WD BLACK 2TB NVME" wird so zu
 * „WD BLACK" / „2TB NVME".
 */
export function titelZeilen(titel: string): readonly string[] {
	const sauber = titel.trim().replace(/\s+/g, " ");

	if (sauber.length <= TITEL_UMBRUCH_AB) {
		return [sauber];
	}

	const mitte = Math.floor(sauber.length / 2);
	let trenner = -1;
	let abstand = Number.MAX_SAFE_INTEGER;

	for (let i = 0; i < sauber.length; i++) {
		if (sauber[i] !== " ") {
			continue;
		}
		const d = Math.abs(i - mitte);
		if (d < abstand) {
			abstand = d;
			trenner = i;
		}
	}

	if (trenner < 0) {
		// Ein einziges langes Wort: hart in der Mitte trennen.
		return [sauber.slice(0, mitte), sauber.slice(mitte)];
	}

	return [sauber.slice(0, trenner), sauber.slice(trenner + 1)];
}

function maskieren(text: string): string {
	return text
		.replace(/&/g, "&amp;")
		.replace(/</g, "&lt;")
		.replace(/>/g, "&gt;")
		.replace(/"/g, "&quot;")
		.replace(/'/g, "&apos;");
}

/**
 * Rechnet die Verlaufswerte auf die Zeichenfläche um.
 *
 * Skaliert wird über die tatsächliche Spanne der Werte, mindestens aber über
 * `mindestspanne`. Ohne diese Untergrenze würde ein Wert, der zwischen 2 % und
 * 3 % schwankt, die volle Höhe ausfüllen und Betriebsamkeit vortäuschen, die
 * es nicht gibt.
 */
function punkte(
	werte: readonly (number | null)[],
	mindestspanne: number,
): readonly ({ x: number; y: number } | null)[] {
	const vorhanden = werte.filter((w): w is number => w !== null);

	if (vorhanden.length === 0) {
		return [];
	}

	const kleinster = Math.min(...vorhanden);
	const groesster = Math.max(...vorhanden);
	const mitte = (kleinster + groesster) / 2;
	const spanne = Math.max(groesster - kleinster, mindestspanne);

	const unten = mitte - spanne / 2;
	const breite = GRAPH_RECHTS - GRAPH_LINKS;
	const hoehe = GRAPH_UNTEN - GRAPH_OBEN;

	// Bei weniger als zwei Werten gäbe es keinen Nenner für die x-Verteilung.
	const nenner = Math.max(1, VERLAUF_LAENGE_FUER_SKALA - 1);

	return werte.map((wert, index) => {
		if (wert === null) {
			return null;
		}

		// Die Reihe wird rechtsbündig gezeichnet: Der neueste Wert sitzt rechts,
		// auch wenn der Verlauf noch nicht voll ist.
		const versatz = VERLAUF_LAENGE_FUER_SKALA - werte.length + index;
		const anteil = (wert - unten) / spanne;

		return {
			x: GRAPH_LINKS + (versatz / nenner) * breite,
			y: GRAPH_UNTEN - Math.min(1, Math.max(0, anteil)) * hoehe,
		};
	});
}

/** Muss zur Länge des Verlaufsspeichers passen. */
const VERLAUF_LAENGE_FUER_SKALA = 60;

function graphSvg(bild: Tastenbild): string {
	const werte = bild.verlauf ?? [];

	if (werte.length < 2) {
		return "";
	}

	const farbe = bild.graphFarbe ?? FARBEN.graph;
	const koordinaten = punkte(werte, bild.graphMindestspanne ?? 1);

	if (koordinaten.length === 0) {
		return "";
	}

	if (bild.graphStil === "balken") {
		const vorhandene = koordinaten.filter((p): p is { x: number; y: number } => p !== null);
		const breite = Math.max(1.2, (GRAPH_RECHTS - GRAPH_LINKS) / VERLAUF_LAENGE_FUER_SKALA - 0.6);

		return vorhandene
			.map(
				(p) =>
					`<rect x="${(p.x - breite / 2).toFixed(1)}" y="${p.y.toFixed(1)}" width="${breite.toFixed(1)}" height="${(GRAPH_UNTEN - p.y).toFixed(1)}" fill="${farbe}"/>`,
			)
			.join("");
	}

	// Linie: Lücken trennen den Streckenzug, statt quer durch das Bild zu
	// springen. Eine fehlende Messung soll als Lücke sichtbar sein.
	const abschnitte: string[] = [];
	let aktuell: string[] = [];

	for (const punkt of koordinaten) {
		if (punkt === null) {
			if (aktuell.length > 1) {
				abschnitte.push(aktuell.join(" "));
			}
			aktuell = [];
			continue;
		}
		aktuell.push(`${punkt.x.toFixed(1)},${punkt.y.toFixed(1)}`);
	}

	if (aktuell.length > 1) {
		abschnitte.push(aktuell.join(" "));
	}

	return abschnitte
		.map(
			(abschnitt) =>
				`<polyline points="${abschnitt}" fill="none" stroke="${farbe}" stroke-width="2.5" stroke-linejoin="round" stroke-linecap="round"/>`,
		)
		.join("");
}

/** Baut das SVG und gibt es als Daten-URI zurück, wie `setImage` es erwartet. */
export function tastenbild(bild: Tastenbild): string {
	const zeilen = titelZeilen(bild.titel);
	const zweizeilig = zeilen.length > 1;
	const titelGroesse = zweizeilig ? 15 : 19;
	const graph = graphSvg(bild);

	const titelSvg = zeilen
		.map((zeile, index) => {
			const y = zweizeilig ? 24 + index * 17 : 30;
			return `<text x="72" y="${y}" fill="${FARBEN.titel}" font-family="${SCHRIFT}" font-size="${titelGroesse}" font-weight="700" text-anchor="middle">${maskieren(zeile)}</text>`;
		})
		.join("");

	// Mit Graph rückt der Wert etwas nach oben, damit beides Platz hat.
	const wertY = graph === "" ? (zweizeilig ? 92 : 86) : zweizeilig ? 84 : 80;

	const svg =
		`<svg xmlns="http://www.w3.org/2000/svg" width="${GROESSE}" height="${GROESSE}" viewBox="0 0 ${GROESSE} ${GROESSE}">` +
		`<rect width="${GROESSE}" height="${GROESSE}" fill="${FARBEN.grund}"/>` +
		titelSvg +
		`<text x="72" y="${wertY}" fill="${wertFarbe(bild.zustand)}" font-family="${SCHRIFT}" font-size="${wertGroesse(bild.wert)}" font-weight="700" text-anchor="middle">${maskieren(bild.wert)}</text>` +
		graph +
		`</svg>`;

	return `data:image/svg+xml;charset=utf8,${encodeURIComponent(svg)}`;
}
