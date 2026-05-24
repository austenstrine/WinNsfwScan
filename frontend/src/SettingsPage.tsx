type SettingsPageProps = {
	protectionIsRequested: boolean
	protectionSecondsRemaining: number
	onRequestDisableProtection: () => void
	onTestHardBlock: () => void
	onManageSubscription: () => void
	ctrlAltHeld: boolean
	onDebugExit: () => void
}

function formatCountdown(totalSeconds: number): string {
	const minutes = Math.floor(totalSeconds / 60)
	const seconds = totalSeconds % 60
	return `${minutes}:${seconds.toString().padStart(2, '0')}`
}

function SettingsPage({ protectionIsRequested, protectionSecondsRemaining, onRequestDisableProtection, onTestHardBlock, onManageSubscription, ctrlAltHeld, onDebugExit }: SettingsPageProps) {
	const protectionDisabled = protectionIsRequested && protectionSecondsRemaining === 0

	return (
		<>
			<div className="flex items-center justify-between mb-8">
				<div>
					<h1 className="text-3xl font-semibold tracking-tight">
						WinNsfwScan
					</h1>
					<p className="text-slate-400 mt-1 text-sm">
						Real-time NSFW Detection
					</p>
				</div>

				<div className="flex items-center gap-2">
					<div className="w-2.5 h-2.5 rounded-full bg-emerald-500" />
					<span className="text-sm text-slate-300">Monitoring</span>
				</div>
			</div>

			<div className="bg-slate-900 border border-slate-800 rounded-2xl p-6 mb-6">
				<h2 className="text-lg font-medium mb-4">Current Status</h2>
				<div className="text-5xl font-semibold tracking-tighter mb-1">Active</div>
				<p className="text-slate-400 text-sm">Last scan: just now</p>
			</div>

			<div className="bg-slate-900 border border-slate-800 rounded-2xl p-6 mb-6">
				<h2 className="text-lg font-medium mb-5">Actions</h2>

				<div className="space-y-4">
					<div className="flex items-center justify-between">
						<div>
							<div className="font-medium">Test hard block</div>
							<div className="text-sm text-slate-400 mt-0.5">
								Trigger a 10-second hard block to verify overlay behaviour
							</div>
						</div>
						<button
							onClick={onTestHardBlock}
							className="ml-4 shrink-0 px-4 py-1.5 text-sm rounded-lg bg-slate-800 hover:bg-slate-700 transition-colors"
						>
							Test
						</button>
					</div>

					<div className="border-t border-slate-800" />

					<div className="flex items-center justify-between">
						<div>
							<div className="font-medium">Subscription</div>
							<div className="text-sm text-slate-400 mt-0.5">
								Manage your WinNsfwScan licence
							</div>
						</div>
						<button
							onClick={onManageSubscription}
							className="ml-4 shrink-0 px-4 py-1.5 text-sm rounded-lg bg-slate-800 hover:bg-slate-700 transition-colors"
						>
							Manage
						</button>
					</div>
				</div>
			</div>

			<div className="bg-slate-900 border border-slate-800 rounded-2xl p-6">
				<h2 className="text-lg font-medium mb-4">App Protection</h2>

				{!protectionIsRequested && (
					<div className="flex items-center justify-between">
						<div>
							<div className="font-medium">Resurrection guard active</div>
							<div className="text-sm text-slate-400 mt-0.5">
								App restarts if closed. Start a 30-min cooldown to disable.
							</div>
						</div>
						<button
							onClick={onRequestDisableProtection}
							className="ml-4 shrink-0 px-4 py-1.5 text-sm rounded-lg bg-red-950 hover:bg-red-900 border border-red-800 text-red-300 transition-colors"
						>
							Disable
						</button>
					</div>
				)}

				{protectionIsRequested && protectionSecondsRemaining > 0 && (
					<div>
						<div className="font-medium">Cooldown active</div>
						<div className="text-sm text-slate-400 mt-1">
							Protection disables in{' '}
							<span className="font-mono text-slate-200 tabular-nums">
								{formatCountdown(protectionSecondsRemaining)}
							</span>
							. The app will still restart if closed before then.
						</div>
					</div>
				)}

				{protectionDisabled && (
					<div className="text-slate-400 text-sm">
						Protection is off — the app can now be closed normally.
					</div>
				)}
			</div>

			{ctrlAltHeld && (
				<div className="mt-6 bg-yellow-950 border border-yellow-800 rounded-2xl p-4 flex items-center justify-between">
					<div>
						<div className="text-sm font-medium text-yellow-300">Debug mode</div>
						<div className="text-xs text-yellow-600 mt-0.5">Bypasses watchdog — process exits immediately</div>
					</div>
					<button
						onClick={onDebugExit}
						className="ml-4 shrink-0 px-4 py-1.5 text-sm rounded-lg bg-yellow-800 hover:bg-yellow-700 text-yellow-100 transition-colors"
					>
						Kill process now
					</button>
				</div>
			)}
		</>
	)
}

export default SettingsPage