import { useState } from 'react'

function App() {
	const [isMonitoring, setIsMonitoring] = useState(true)

	return (
		<div className="min-h-screen bg-slate-950 text-slate-200 p-8">
			<div className="max-w-2xl mx-auto">
				{/* Header */}
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
						<div className={`w-2.5 h-2.5 rounded-full ${isMonitoring ? 'bg-emerald-500' : 'bg-red-500'}`} />
						<span className="text-sm text-slate-300">
							{isMonitoring ? 'Monitoring' : 'Paused'}
						</span>
					</div>
				</div>

				{/* Status Card */}
				<div className="bg-slate-900 border border-slate-800 rounded-2xl p-6 mb-6">
					<div className="flex items-center justify-between mb-4">
						<h2 className="text-lg font-medium">
              Current Status
            </h2>
						<button
							onClick={() => setIsMonitoring(!isMonitoring)}
							className="px-4 py-1.5 text-sm rounded-lg bg-slate-800 hover:bg-slate-700 transition-colors"
						>
							{isMonitoring ? 'Pause' : 'Resume'}
						</button>
					</div>

					<div className="text-5xl font-semibold tracking-tighter mb-1">
						{isMonitoring ? 'Active' : 'Paused'}
					</div>
					<p className="text-slate-400 text-sm">Last scan: just now</p>
				</div>

				{/* Settings */}
				<div className="bg-slate-900 border border-slate-800 rounded-2xl p-6">
					<h2 className="text-lg font-medium mb-5">
            Settings
          </h2>

					<div className="space-y-5">
						<div className="flex items-center justify-between">
							<div>
								<div className="font-medium">
                  Start minimized to tray
                </div>
								<div className="text-sm text-slate-400">
                  Launch automatically with Windows
                </div>
							</div>
							<input type="checkbox" className="w-5 h-5 accent-emerald-600" defaultChecked />
						</div>

						<div className="flex items-center justify-between">
							<div>
								<div className="font-medium">
                  Show detection notifications
                </div>
								<div className="text-sm text-slate-400">
                  Desktop toast when NSFW is detected
                </div>
							</div>
							<input type="checkbox" className="w-5 h-5 accent-emerald-600" defaultChecked />
						</div>

						<div className="flex items-center justify-between">
							<div>
								<div className="font-medium">
                  Detection sensitivity
                </div>
								<div className="text-sm text-slate-400">
                  How strict the detection should be
                </div>
							</div>
							<select className="bg-slate-800 border border-slate-700 rounded-lg px-3 py-1.5 text-sm">
								<option>Balanced</option>
								<option>Strict</option>
								<option>Lenient</option>
							</select>
						</div>
					</div>
				</div>

				<div className="text-center mt-8 text-xs text-slate-500">
					Press <span className="font-mono">F12</span> to open DevTools
				</div>
			</div>
		</div>
	)
}

export default App