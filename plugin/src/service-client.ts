/**
 * Ein einziger Abruf der Dienst-API für alle Tasten.
 *
 * Projektvorgabe: ein gemeinsamer Poll, nicht einer je Taste. Zehn Tasten
 * dürfen nicht zehnmal pro Sekunde fragen - die API liest zwar nur ihren
 * Zwischenspeicher, aber Netzwerkaufrufe und JSON-Parsen sind trotzdem Arbeit.
 */

/** Ein Sensor, wie ihn die API liefert. */
export type Sensor = {
	readonly id: string;
	readonly name: string;
	readonly category: string;
	readonly value: number | null;
	readonly unit: string;
	readonly available: boolean;
	readonly updatedAt: string;
};

type Snapshot = {
	readonly generatedAt: string;
	readonly version: string;
	readonly sensors: readonly Sensor[];
};

/** Wird nach jedem Abruf gerufen, auch wenn er fehlgeschlagen ist. */
export type Listener = () => void;

const BASIS = "http://127.0.0.1:5891";
const TAKT_MS = 1000;

/**
 * Nach dieser Zeit wird ein Abruf abgebrochen. Kürzer als der Takt, damit sich
 * hängende Anfragen nicht stapeln können.
 */
const ZEITLIMIT_MS = 800;

class ServiceClient {
	private readonly sensoren = new Map<string, Sensor>();
	private readonly listener = new Set<Listener>();
	private timer: NodeJS.Timeout | undefined;
	private laeuft = false;
	private erreichbar = false;

	/** Version des Dienstes, sobald er geantwortet hat. */
	public version: string | undefined;

	/** Ob der letzte Abruf geklappt hat. */
	public get istErreichbar(): boolean {
		return this.erreichbar;
	}

	/** Alle bekannten Sensoren, in der Reihenfolge der API. */
	public get alle(): readonly Sensor[] {
		return [...this.sensoren.values()];
	}

	public sensor(id: string): Sensor | undefined {
		return this.sensoren.get(id);
	}

	public start(): void {
		if (this.timer !== undefined) {
			return;
		}

		void this.abrufen();
		this.timer = setInterval(() => void this.abrufen(), TAKT_MS);
	}

	public stop(): void {
		if (this.timer !== undefined) {
			clearInterval(this.timer);
			this.timer = undefined;
		}
	}

	public abonnieren(listener: Listener): () => void {
		this.listener.add(listener);
		return () => this.listener.delete(listener);
	}

	private async abrufen(): Promise<void> {
		// Überlappende Abrufe überspringen statt stapeln - dieselbe Regel wie
		// im Dienst selbst.
		if (this.laeuft) {
			return;
		}

		this.laeuft = true;

		try {
			const antwort = await fetch(`${BASIS}/api/sensors`, {
				signal: AbortSignal.timeout(ZEITLIMIT_MS),
			});

			if (!antwort.ok) {
				this.erreichbar = false;
			} else {
				const stand = (await antwort.json()) as Snapshot;
				this.version = stand.version;

				this.sensoren.clear();
				for (const sensor of stand.sensors) {
					this.sensoren.set(sensor.id, sensor);
				}

				this.erreichbar = true;
			}
		} catch {
			// Dienst läuft nicht, startet gerade neu, oder die Anfrage lief in
			// das Zeitlimit. Alles kein Grund für eine Fehlermeldung je Sekunde:
			// Die Tasten zeigen „--" und es wird still weiter versucht.
			this.erreichbar = false;
		} finally {
			this.laeuft = false;
		}

		for (const listener of this.listener) {
			try {
				listener();
			} catch {
				// Ein kaputter Zuhörer darf die anderen nicht mitreißen.
			}
		}
	}
}

export const service = new ServiceClient();
