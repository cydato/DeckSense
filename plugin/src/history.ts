/**
 * Verlauf der Sensorwerte, für den Graphen auf der Taste.
 *
 * Der Verlauf liegt im Plugin, nicht im Dienst - so festgelegt in
 * Entscheidung 0005. Der Dienst bleibt dadurch zustandsarm, und ein
 * Dienstneustart erzeugt im Graphen eine Lücke statt eines Fehlers.
 *
 * Gespeichert wird **je Sensor**, nicht je Taste: Zwei Tasten, die denselben
 * Sensor zeigen, teilen sich einen Verlauf.
 */

/** So viele Werte hält der Graph vor, also etwa eine Minute bei 1 s Takt. */
export const VERLAUF_LAENGE = 60;

class Verlauf {
	private readonly werte = new Map<string, (number | null)[]>();

	/**
	 * Nimmt einen Wert auf. `null` steht für „nicht verfügbar" und erzeugt im
	 * Graphen eine Lücke - anders als eine 0, die eine Aussage wäre.
	 */
	public aufnehmen(sensorId: string, wert: number | null): void {
		let reihe = this.werte.get(sensorId);

		if (reihe === undefined) {
			reihe = [];
			this.werte.set(sensorId, reihe);
		}

		reihe.push(wert);

		if (reihe.length > VERLAUF_LAENGE) {
			reihe.splice(0, reihe.length - VERLAUF_LAENGE);
		}
	}

	public reihe(sensorId: string): readonly (number | null)[] {
		return this.werte.get(sensorId) ?? [];
	}

	/**
	 * Entfernt Verläufe, die keine Taste mehr braucht. Ohne das würde der
	 * Speicher mit jedem ausprobierten Sensor wachsen.
	 */
	public aufraeumen(gebraucht: ReadonlySet<string>): void {
		for (const id of [...this.werte.keys()]) {
			if (!gebraucht.has(id)) {
				this.werte.delete(id);
			}
		}
	}
}

export const verlauf = new Verlauf();
