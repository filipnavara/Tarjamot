// Usage: node summary-n.js <bench-env.sh result file>
// Per case and variant: median wall and CPU time across processes, with each process's median.
const lines = require('fs').readFileSync(process.argv[2], 'utf8').split('\n').filter(l => l.includes('median_ms='));
const data = {}, order = [];
for (const l of lines) {
	const [name, variant] = l.split(' ');
	const kv = Object.fromEntries([...l.matchAll(/(\w+)=([\d.]+)/g)].map(m => [m[1], +m[2]]));
	if (!order.includes(variant)) order.push(variant);
	((data[name] ??= {})[variant] ??= []).push(kv);
}
const med = a => { const s = [...a].sort((x, y) => x - y); return s[Math.floor(s.length / 2)]; };
for (const [name, variants] of Object.entries(data)) {
	console.log(`== ${name}`);
	for (const v of order) {
		const r = variants[v];
		if (r)
			console.log(`  ${v.padEnd(14)} wall ${med(r.map(x => x.median_ms)).toFixed(1).padStart(8)} (${r.map(x => x.median_ms.toFixed(1)).join(', ')})  cpu ${med(r.map(x => x.median_cpu_ms)).toFixed(0).padStart(6)}`);
	}
}
