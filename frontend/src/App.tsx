import { useState } from 'react'

function App() {
  const [backendRunning, setBackendRunning] = useState(true)
  const [lastScan, setLastScan] = useState('just now')

  return (
    <div className="min-h-screen bg-slate-950 text-slate-200">
      <div className="max-w-3xl mx-auto p-8">
        {/* Header */}
        <div className="flex items-center justify-between mb-10">
          <div>
            <h1 className="text-4xl font-bold tracking-tight">WinNsfwScan</h1>
            <p className="text-slate-400 mt-1">Real-time NSFW Detection for Windows</p>
          </div>

          <div className="flex items-center gap-3">
            <div className={`w-3 h-3 rounded-full ${backendRunning ? 'bg-emerald-500' : 'bg-red-500'}`} />
            <span className="text-sm font-medium">
              {backendRunning ? 'Backend Online' : 'Backend Offline'}
            </span>
          </div>
        </div>

        {/* Status Card */}
        <div className="bg-slate-900 border border-slate-800 rounded-2xl p-6 mb-6">
          <div className="flex items-center justify-between mb-4">
            <h2 className="text-xl font-semibold">Current Status</h2>
            <button
              onClick={() => setBackendRunning(!backendRunning)}
              className="px-4 py-1.5 text-sm bg-slate-800 hover:bg-slate-700 rounded-lg transition-colors"
            >
              Toggle Status
            </button>
          </div>

          <div className="text-5xl font-bold mb-2">
            {backendRunning ? 'Monitoring' : 'Paused'}
          </div>
          <p className="text-slate-400">Last scan: {lastScan}</p>
        </div>

        {/* Settings */}
        <div className="bg-slate-900 border border-slate-800 rounded-2xl p-6">
          <h2 className="text-xl font-semibold mb-5">Settings</h2>

          <div className="space-y-5">
            <div className="flex items-center justify-between">
              <div>
                <div className="font-medium">Start minimized to tray</div>
                <div className="text-sm text-slate-400">Launch automatically with Windows</div>
              </div>
              <input type="checkbox" className="w-5 h-5 accent-emerald-600" defaultChecked />
            </div>

            <div className="flex items-center justify-between">
              <div>
                <div className="font-medium">Show detection notifications</div>
                <div className="text-sm text-slate-400">Desktop toast when NSFW is detected</div>
              </div>
              <input type="checkbox" className="w-5 h-5 accent-emerald-600" defaultChecked />
            </div>

            <div className="flex items-center justify-between">
              <div>
                <div className="font-medium">Detection sensitivity</div>
                <div className="text-sm text-slate-400">Adjust how strict the detection is</div>
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