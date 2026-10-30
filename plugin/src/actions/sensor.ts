import streamDeck, {
	action,
	SingletonAction,
	type DidReceiveSettingsEvent,
	type SendToPluginEvent,
	type WillAppearEvent,
	type WillDisappearEvent,
} from "@elgato/streamdeck";
import type { JsonValue } from "@elgato/utils";

import { graphMindestspanne, wertText, zustand } from "../format.js";
import { verlauf } from "../history.js";
import { tastenbild, type GraphStil } from "../render.js";
import { service, type Sensor } from "../service-client.js";

/** Was zu einer Taste gespeichert wird. */
export type SensorSettings = {
	/** Sensor-Id aus der API, etwa `cpu.usage`. */
	sensorId?: string;
	/** Frei gewählter Titel. Leer heißt: Name aus der API nehmen. */
	titel?: string;
	/** Verlaufsgraph anzeigen. Vorgabe: ja. */
	graph?: boolean;
	graphStil?: GraphStil;
	/** Farbe des Graphen als Hex. Leer heißt: Hausfarbe. */
	graphFarbe?: string;
	/** Schwellen für die Zustandsfarbe. Leer heißt: Vorgabe der Einheit. */
	warnAb?: number;
	kritischAb?: number;
};

@action({ UUID: "com.cydato.decksense.sensor" })
export class SensorAction extends SingletonAction<SensorSettings> {
	/**
	 * Einstellungen je sichtbarer Taste.
	 *
	 * Gehalten wird das hier, damit der Takt die Einstellungen nicht jede
	 * Sekunde einzeln bei Stream Deck erfragen muss - das wären bei zehn Tasten
	 * zehn zusätzliche Anfragen pro Sekunde, nur um zu wissen, was ohnehin
	 * bekannt ist.
	 */
	private readonly einstellungen = new Map<string, SensorSettings>();

	public override onWillAppear(ev: WillAppearEvent<SensorSettings>): Promise<void> | void {
		this.einstellungen.set(ev.action.id, ev.payload.settings ?? {});
		return this.zeichnen(ev.action.id);
	}

	public override onWillDisappear(ev: WillDisappearEvent<SensorSettings>): Promise<void> | void {
		this.einstellungen.delete(ev.action.id);
	}

	public override onDidReceiveSettings(ev: DidReceiveSettingsEvent<SensorSettings>): Promise<void> | void {
		this.einstellungen.set(ev.action.id, ev.payload.settings ?? {});
		return this.zeichnen(ev.action.id);
	}

	/**
	 * Der Property Inspector fragt die Sensorliste an. Sie kommt über das
	 * Plugin und nicht direkt aus der API: Der Property Inspector läuft in
	 * einem Webview mit fremder Herkunft, ein Zugriff auf 127.0.0.1 wäre dort
	 * ein anderer Ursprung. Der API dafür CORS beizubringen würde bedeuten,
	 * dass jede beliebige Webseite im Browser des Nutzers die Sensorwerte
	 * auslesen kann - das ist den Komfort nicht wert.
	 */
	public override async onSendToPlugin(ev: SendToPluginEvent<JsonValue, SensorSettings>): Promise<void> {
		const anfrage = ev.payload as { event?: string } | null;

		if (anfrage?.event !== "getSensors") {
			return;
		}

		await streamDeck.ui.sendToPropertyInspector({
			event: "sensors",
			erreichbar: service.istErreichbar,
			version: service.version ?? null,
			items: service.alle.map((sensor) => ({
				label: `${sensor.name}  (${sensor.id})`,
				value: sensor.id,
				unit: sensor.unit,
			})),
		});
	}

	/**
	 * Nimmt den Verlauf auf und zeichnet alle sichtbaren Tasten neu. Wird vom
	 * gemeinsamen Takt gerufen.
	 */
	public async alleZeichnen(): Promise<void> {
		// Verlauf je Sensor, nicht je Taste: Zwei Tasten mit demselben Sensor
		// teilen sich eine Reihe.
		const gebraucht = new Set<string>();

		for (const einstellung of this.einstellungen.values()) {
			if (einstellung.sensorId !== undefined) {
				gebraucht.add(einstellung.sensorId);
			}
		}

		for (const id of gebraucht) {
			const sensor = service.sensor(id);
			verlauf.aufnehmen(id, sensor?.available === true ? sensor.value : null);
		}

		verlauf.aufraeumen(gebraucht);

		for (const taste of this.actions) {
			await this.zeichnen(taste.id);
		}
	}

	private async zeichnen(id: string): Promise<void> {
		const taste = [...this.actions].find((a) => a.id === id);

		if (taste === undefined || !taste.isKey()) {
			return;
		}

		const einstellung = this.einstellungen.get(id) ?? {};
		const sensor: Sensor | undefined =
			einstellung.sensorId === undefined ? undefined : service.sensor(einstellung.sensorId);

		const titel = einstellung.titel?.trim()
			? einstellung.titel.trim()
			: (sensor?.name ?? (einstellung.sensorId === undefined ? "Kein Sensor" : einstellung.sensorId));

		// Graph ist Vorgabe; nur ein ausdrückliches false schaltet ihn ab.
		const graphAn = einstellung.graph !== false && einstellung.sensorId !== undefined;

		try {
			await taste.setImage(
				tastenbild({
					titel,
					wert: wertText(sensor),
					zustand: zustand(sensor, einstellung),
					verlauf: graphAn ? verlauf.reihe(einstellung.sensorId!) : undefined,
					graphStil: einstellung.graphStil ?? "linie",
					graphFarbe: einstellung.graphFarbe?.trim() || undefined,
					graphMindestspanne: graphMindestspanne(sensor?.unit),
				}),
			);
		} catch (fehler) {
			// Eine Taste, die gerade verschwindet, darf den Takt nicht stören.
			streamDeck.logger.debug(`Taste ${id} konnte nicht gezeichnet werden: ${String(fehler)}`);
		}
	}
}
