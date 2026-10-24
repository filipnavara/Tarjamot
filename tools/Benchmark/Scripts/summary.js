// Usage: node summary.js <bench.sh result file>
// Per case, baseline / candidate (in the order of the bin names): medians across processes of the
// harness metrics, and each process's median wall time.
const lines = require('fs').readFileSync(process.argv[2], 'utf8').split('\n').filter(l => l.includes('median_ms='));
const data = {};
for (const l of lines) {
	const [name, bin] = l.split(' ');
	const kv = Object.fromEntries([...l.matchAll(/(\w+)=([\d.]+)/g)].map(m => [m[1], +m[2]]));
	((data[name] ??= {})[bin] ??= []).push(kv);
}
const med = a => { const s = [...a].sort((x, y) => x - y); return s[Math.floor(s.length / 2)]; };
const MB = b => (b / 1048576).toFixed(1);
console.log('| Case | Wall ms A / B (processes) | CPU ms A / B | Alloc/run KB A / B | Live MB A / B | Peak WS MB A / B | Cold ms A / B |');
console.log('|---|---|---|---|---|---|---|');
for (const [name, bins] of Object.entries(data)) {
	const [a, b] = Object.keys(bins);
	const A = bins[a], C = bins[b];
	const f = (k, fn = x => x.toFixed(1)) => `${fn(med(A.map(r => r[k])))} / ${fn(med(C.map(r => r[k])))}`;
	const runs = `${A.map(r => r.median_ms.toFixed(1)).join(', ')} vs ${C.map(r => r.median_ms.toFixed(1)).join(', ')}`;
	console.log(`| ${name} | ${f('median_ms')} (${runs}) | ${f('median_cpu_ms')} | ${f('alloc_per_run', x => (x / 1024).toFixed(0))} | ${f('live_bytes', MB)} | ${f('peak_ws', MB)} | ${f('cold_ms', x => x.toFixed(0))} |`);
}
