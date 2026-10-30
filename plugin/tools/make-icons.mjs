// Erzeugt die PNG-Symbole für das Plugin.
//
// Warum ein eigener Generator: Stream Deck erwartet PNG, und Binärdateien
// gehören nicht von Hand ins Repository. Das Skript baut sie aus Node-Bordmitteln
// (zlib), ist nachvollziehbar und jederzeit wiederholbar:
//
//     node tools/make-icons.mjs
//
// Motiv: violetter Grund (#7800ff) mit drei weißen Balken - ein Miniaturdiagramm.

import { deflateSync } from "node:zlib";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";

const VIOLETT = [0x78, 0x00, 0xff];
const WEISS = [0xff, 0xff, 0xff];
const SCHWARZ = [0x00, 0x00, 0x00];

const crcTable = (() => {
	const table = new Int32Array(256);
	for (let n = 0; n < 256; n++) {
		let c = n;
		for (let k = 0; k < 8; k++) {
			c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
		}
		table[n] = c;
	}
	return table;
})();

function crc32(buffer) {
	let c = 0xffffffff;
	for (const byte of buffer) {
		c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
	}
	return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
	const length = Buffer.alloc(4);
	length.writeUInt32BE(data.length, 0);

	const typeAndData = Buffer.concat([Buffer.from(type, "ascii"), data]);

	const crc = Buffer.alloc(4);
	crc.writeUInt32BE(crc32(typeAndData), 0);

	return Buffer.concat([length, typeAndData, crc]);
}

/** Baut ein PNG aus einem RGB-Puffer (3 Byte je Pixel, zeilenweise). */
function png(width, height, rgb) {
	const signature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

	const ihdr = Buffer.alloc(13);
	ihdr.writeUInt32BE(width, 0);
	ihdr.writeUInt32BE(height, 4);
	ihdr[8] = 8; // Bittiefe
	ihdr[9] = 2; // Farbtyp 2 = Truecolor RGB
	ihdr[10] = 0; // Kompression
	ihdr[11] = 0; // Filter
	ihdr[12] = 0; // kein Interlacing

	// Jede Zeile bekommt ein führendes Filterbyte 0 (ohne Filter).
	const raw = Buffer.alloc(height * (1 + width * 3));
	for (let y = 0; y < height; y++) {
		const target = y * (1 + width * 3);
		raw[target] = 0;
		rgb.copy(raw, target + 1, y * width * 3, (y + 1) * width * 3);
	}

	return Buffer.concat([
		signature,
		chunk("IHDR", ihdr),
		chunk("IDAT", deflateSync(raw, { level: 9 })),
		chunk("IEND", Buffer.alloc(0)),
	]);
}

/** Zeichnet das Motiv in einen RGB-Puffer. */
function motiv(size, { grund, balken }) {
	const rgb = Buffer.alloc(size * size * 3);

	const setzen = (x, y, farbe) => {
		if (x < 0 || y < 0 || x >= size || y >= size) {
			return;
		}
		const offset = (y * size + x) * 3;
		rgb[offset] = farbe[0];
		rgb[offset + 1] = farbe[1];
		rgb[offset + 2] = farbe[2];
	};

	for (let y = 0; y < size; y++) {
		for (let x = 0; x < size; x++) {
			setzen(x, y, grund);
		}
	}

	// Drei Balken mit 25 %, 60 % und 90 % Höhe, auf einer gemeinsamen Grundlinie.
	const anteile = [0.25, 0.6, 0.9];
	const rand = Math.max(2, Math.round(size * 0.18));
	const grundlinie = size - rand;
	const breite = Math.max(2, Math.round(size * 0.14));
	const abstand = Math.max(1, Math.round(size * 0.08));
	const gesamt = anteile.length * breite + (anteile.length - 1) * abstand;
	let x0 = Math.round((size - gesamt) / 2);

	for (const anteil of anteile) {
		const hoehe = Math.max(2, Math.round((grundlinie - rand) * anteil));
		for (let y = grundlinie - hoehe; y < grundlinie; y++) {
			for (let x = x0; x < x0 + breite; x++) {
				setzen(x, y, balken);
			}
		}
		x0 += breite + abstand;
	}

	return rgb;
}

const dateien = [
	// Symbol des Plugins, von Stream Deck in der Plugin-Liste gezeigt.
	["imgs/plugin/marketplace.png", 72, { grund: VIOLETT, balken: WEISS }],
	["imgs/plugin/marketplace@2x.png", 144, { grund: VIOLETT, balken: WEISS }],

	// Symbol der Kategorie in der Aktionsliste.
	["imgs/plugin/category-icon.png", 28, { grund: SCHWARZ, balken: VIOLETT }],
	["imgs/plugin/category-icon@2x.png", 56, { grund: SCHWARZ, balken: VIOLETT }],

	// Symbol der Aktion in der Aktionsliste.
	["imgs/actions/sensor/icon.png", 20, { grund: SCHWARZ, balken: VIOLETT }],
	["imgs/actions/sensor/icon@2x.png", 40, { grund: SCHWARZ, balken: VIOLETT }],

	// Standardbild der Taste, bevor der Dienst den ersten Wert liefert.
	["imgs/actions/sensor/key.png", 72, { grund: SCHWARZ, balken: VIOLETT }],
	["imgs/actions/sensor/key@2x.png", 144, { grund: SCHWARZ, balken: VIOLETT }],
];

const wurzel = join(import.meta.dirname, "..", "com.cydato.decksense.sdPlugin");

for (const [pfad, groesse, farben] of dateien) {
	const ziel = join(wurzel, pfad);
	mkdirSync(dirname(ziel), { recursive: true });
	writeFileSync(ziel, png(groesse, groesse, motiv(groesse, farben)));
	console.log(`${pfad}  ${groesse}x${groesse}`);
}

console.log(`\n${dateien.length} Symbole erzeugt.`);
