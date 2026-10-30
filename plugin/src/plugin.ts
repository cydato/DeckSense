import streamDeck from "@elgato/streamdeck";

import { SensorAction } from "./actions/sensor.js";
import { service } from "./service-client.js";

const sensor = new SensorAction();

streamDeck.actions.registerAction(sensor);

await streamDeck.connect();

streamDeck.logger.info("DeckSense-Plugin verbunden. Frage den Dienst auf 127.0.0.1:5891 ab.");

/**
 * Ein gemeinsamer Takt für alle Tasten. Nach jedem Abruf werden die sichtbaren
 * Tasten neu gezeichnet - auch dann, wenn der Abruf fehlgeschlagen ist, denn
 * dann sollen sie „--" zeigen.
 */
let zuletztErreichbar: boolean | undefined;

service.abonnieren(() => {
	if (service.istErreichbar !== zuletztErreichbar) {
		zuletztErreichbar = service.istErreichbar;
		streamDeck.logger.info(
			service.istErreichbar
				? `Dienst erreichbar, Version ${service.version ?? "unbekannt"}.`
				: "Dienst nicht erreichbar. Die Tasten zeigen „--\", es wird still weiter versucht.",
		);
	}

	void sensor.alleZeichnen();
});

service.start();
