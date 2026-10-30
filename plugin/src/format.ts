import type { Sensor } from "./service-client.js";
import type { Zustand } from "./render.js";

/**
 * Formatiert Werte so, wie sie bisher auf den Tasten standen: ganzzahlig,
 * Prozent ohne Leerzeichen, alles andere mit. Also „2%", „47 °C", „15249 MB",
 * „31 FPS", „7 ms".
 */
export function wertText(sensor: Sensor | undefined): string {
	if (sensor === undefined || !sensor.available || sensor.value === null) {
		return "--";
	}

	const zahl = Math.round(sensor.value).toString();
	return sensor.unit === "%" ? `${zahl}%` : `${zahl} ${sensor.unit}`;
}

/** Je Taste einstellbare Schwellen. Leer heißt: Vorgabe der Einheit. */
export type Schwellen = {
	readonly warnAb?: number;
	readonly kritischAb?: number;
};

/**
 * Vorgaben gelten nur für Temperaturen. Eine CPU bei 95 % Last ist nicht in
 * Not, eine SSD bei 95 °C schon - deshalb bekommen Prozentwerte ohne
 * ausdrückliche Einstellung keine Warnfarbe.
 *
 * Die Werte sind Hausnummern mit Begründung: NVMe-Laufwerke drosseln
 * typischerweise ab etwa 70 °C, und 85 °C ist bei den meisten Modellen die
 * Grenze, ab der es ernst wird.
 */
const VORGABE_TEMPERATUR = { warnAb: 70, kritischAb: 85 } as const;

export function zustand(sensor: Sensor | undefined, schwellen?: Schwellen): Zustand {
	if (sensor === undefined || !sensor.available || sensor.value === null) {
		return "fehlt";
	}

	const vorgabe = sensor.unit === "°C" ? VORGABE_TEMPERATUR : undefined;
	const warnAb = schwellen?.warnAb ?? vorgabe?.warnAb;
	const kritischAb = schwellen?.kritischAb ?? vorgabe?.kritischAb;

	if (kritischAb !== undefined && sensor.value >= kritischAb) {
		return "kritisch";
	}

	if (warnAb !== undefined && sensor.value >= warnAb) {
		return "warnung";
	}

	return "normal";
}

/**
 * Kleinste Spanne, über die der Graph skaliert. Ohne diese Untergrenze würde
 * ein Wert, der zwischen 2 % und 3 % schwankt, die volle Höhe ausfüllen und
 * Betriebsamkeit vortäuschen, die es nicht gibt.
 */
export function graphMindestspanne(einheit: string | undefined): number {
	switch (einheit) {
		case "%":
			return 10;
		case "°C":
			return 5;
		case "FPS":
			return 20;
		case "ms":
			return 5;
		case "MB":
			return 500;
		default:
			return 1;
	}
}
