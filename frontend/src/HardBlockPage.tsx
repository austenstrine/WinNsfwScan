type HardBlockPageProps = {
	secondsRemaining: number
}

function HardBlockPage({ secondsRemaining }: HardBlockPageProps) {
	const safeSeconds = Math.max(0, secondsRemaining)

	return (
		<div className="min-h-[70vh] flex items-center justify-center">
			<div className="w-full max-w-lg rounded-3xl border border-rose-500/30 bg-slate-900/95 px-8 py-10 shadow-2xl shadow-rose-950/30">
				<div className="inline-flex items-center gap-2 rounded-full border border-rose-500/30 bg-rose-500/10 px-3 py-1 text-xs uppercase tracking-[0.24em] text-rose-200">
					Hard Block
				</div>

				<h1 className="mt-6 text-4xl font-semibold tracking-tight text-white">
					Content blocked
				</h1>

				<p className="mt-4 text-sm leading-6 text-slate-300">
					The current window was hidden for safety. This page will return to normal automatically.
				</p>

				<div className="mt-8 rounded-2xl border border-slate-800 bg-slate-950/70 px-6 py-5">
					<div className="text-sm uppercase tracking-[0.24em] text-slate-500">
						Release in
					</div>
					<div className="mt-2 text-7xl font-semibold tracking-tighter text-white">
						{safeSeconds}
					</div>
					<div className="mt-2 text-sm text-slate-400">
						seconds
					</div>
				</div>
			</div>
		</div>
	)
}

export default HardBlockPage