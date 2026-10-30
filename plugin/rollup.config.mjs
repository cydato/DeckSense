import commonjs from "@rollup/plugin-commonjs";
import nodeResolve from "@rollup/plugin-node-resolve";
import typescript from "@rollup/plugin-typescript";

const sdPlugin = "com.cydato.decksense.sdPlugin";

/**
 * Bündelt das Plugin zu einer einzigen Datei unter bin/. Stream Deck startet
 * genau diese Datei mit dem Node-Laufzeitsystem, das im Manifest steht.
 */
export default {
	input: "src/plugin.ts",
	output: {
		file: `${sdPlugin}/bin/plugin.js`,
		format: "esm",
		sourcemap: true,
		sourcemapPathTransform: (relative) => relative.replace(/^\.\.\//, ""),
	},
	plugins: [
		typescript({ tsconfig: "./tsconfig.json", noForceEmit: true }),
		nodeResolve({ browser: false, exportConditions: ["node"], preferBuiltins: true }),
		commonjs(),
	],

	// Nichts externalisieren außer den eingebauten Node-Modulen. Neben dem
	// Plugin liegt kein node_modules, also muss alles ins Bündel - auch `ws`,
	// von dem das SDK abhängt. Wird es externalisiert, startet das Plugin nicht
	// und Stream Deck meldet nur „Process stopped (unexpected): code=1".
	external: [],

	onwarn(warnung, weiter) {
		// `ws` lädt optionale native Beschleuniger über require() und fällt
		// ohne sie auf reines JavaScript zurück. Die Warnung ist hier normal.
		if (warnung.code === "UNRESOLVED_IMPORT" || warnung.code === "CIRCULAR_DEPENDENCY") {
			return;
		}
		weiter(warnung);
	},
};
