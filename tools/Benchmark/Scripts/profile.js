// Usage: node profile.js <speedscope.json> [root regex] [callers regex]
// For a `dotnet-trace collect --profile cpu-sampling --format Speedscope` trace: time under the
// root frame (default MarianTranslator.TranslateBatch) by inclusive share per frame; with a third
// argument, the call chains (three frames up) of the frames matching it instead.
const f = JSON.parse(require('fs').readFileSync(process.argv[2]));
const frames = f.shared.frames.map(x => x.name);
const root = new RegExp(process.argv[3] || 'MarianTranslator\.TranslateBatch');
const callers = process.argv[4] ? new RegExp(process.argv[4]) : null;
const short = n => n.replace(/^[^!]*!/, '').replace(/Tarjamot\.(Engine\.)?/g, '').replace(/\(.*$/, '');
const inclusive = new Map(), chains = new Map();
let total = 0;
for (const p of f.profiles) {
	const stack = [];
	let last = 0;
	for (const e of p.events) {
		const dt = e.at - last;
		last = e.at;
		const start = stack.findIndex(s => root.test(frames[s]));
		if (dt > 0 && start >= 0) {
			total += dt;
			for (const s of new Set(stack.slice(start)))
				inclusive.set(s, (inclusive.get(s) || 0) + dt);
			const i = callers ? stack.findIndex(s => callers.test(frames[s])) : -1;
			if (i >= 0) {
				const key = stack.slice(Math.max(0, i - 3), i + 1).map(s => short(frames[s]).slice(0, 80)).join(' > ');
				chains.set(key, (chains.get(key) || 0) + dt);
			}
		}
		if (e.type === 'O') stack.push(e.frame); else stack.pop();
	}
}
const top = (m, n) => [...m].sort((a, b) => b[1] - a[1]).slice(0, n);
console.log(`root time ${total.toFixed(0)} ms`);
if (callers)
	for (const [k, v] of top(chains, 15)) console.log(`${(100 * v / total).toFixed(2).padStart(6)}% ${k}`);
else
	for (const [k, v] of top(inclusive, 30)) console.log(`${(100 * v / total).toFixed(1).padStart(5)}% ${short(frames[k])}`);
