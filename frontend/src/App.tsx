import { useEffect, useState } from 'react'
import HardBlockPage from './HardBlockPage'
import SettingsPage from './SettingsPage'

type ViewMode = 'settings' | 'hard-block'

type HardBlockMessage =
	| { type: 'hard-block-start'; durationSeconds?: number }
	| { type: 'hard-block-end' }

type WebViewBridge = {
	addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
	removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

const DEFAULT_HARD_BLOCK_SECONDS = 10

function App() {
	const [isMonitoring, setIsMonitoring] = useState(true)
	const [viewMode, setViewMode] = useState<ViewMode>('settings')
	const [hardBlockSecondsRemaining, setHardBlockSecondsRemaining] = useState(DEFAULT_HARD_BLOCK_SECONDS)

	useEffect(() => {
		const webView = (window as Window & { chrome?: { webview?: WebViewBridge } }).chrome?.webview
		if(!webView) {
			return
		}

		const handleMessage = (event: MessageEvent) => {
			const message = event.data as HardBlockMessage | string | undefined

			if(typeof message === 'string') {
				return
			}

			if(!message || typeof message !== 'object') {
				return
			}

			if(message.type === 'hard-block-start') {
				const durationSeconds = Math.max(1, Math.floor(message.durationSeconds ?? DEFAULT_HARD_BLOCK_SECONDS))
				setHardBlockSecondsRemaining(durationSeconds)
				setViewMode('hard-block')
				return
			}

			if(message.type === 'hard-block-end') {
				setViewMode('settings')
				setHardBlockSecondsRemaining(DEFAULT_HARD_BLOCK_SECONDS)
			}
		}

		webView.addEventListener('message', handleMessage)
		return () => webView.removeEventListener('message', handleMessage)
	}, [])

	useEffect(() => {
		if(viewMode !== 'hard-block') {
			return
		}

		if(hardBlockSecondsRemaining <= 0) {
			return
		}

		const timer = window.setInterval(() => {
			setHardBlockSecondsRemaining((current) => Math.max(0, current - 1))
		}, 1000)

		return () => window.clearInterval(timer)
	}, [hardBlockSecondsRemaining, viewMode])

	return (
		<div className="min-h-screen bg-slate-950 text-slate-200 p-8">
			<div className="max-w-2xl mx-auto">
				{viewMode === 'hard-block' ? (
					<HardBlockPage secondsRemaining={hardBlockSecondsRemaining} />
				) : (
					<SettingsPage isMonitoring={isMonitoring} onToggleMonitoring={() => setIsMonitoring(!isMonitoring)} />
				)}

				<div className="text-center mt-8 text-xs text-slate-500">
					Press <span className="font-mono">F12</span> to open DevTools
				</div>
			</div>
		</div>
	)
}

export default App