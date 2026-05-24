import { useCallback, useEffect, useState } from 'react'
import HardBlockPage from './HardBlockPage'
import SettingsPage from './SettingsPage'

type ViewMode = 'settings' | 'hard-block'

type HardBlockMessage =
	| { type: 'hard-block-start'; durationSeconds?: number }
	| { type: 'hard-block-end' }

type ProtectionStateMessage = {
	type: 'protection-state'
	isRequested: boolean
	secondsRemaining: number
}

type CtrlAltStateMessage = {
	type: 'ctrl-alt-state'
	held: boolean
}

type IncomingMessage = HardBlockMessage | ProtectionStateMessage | CtrlAltStateMessage

type WebViewBridge = {
	addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
	removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
	postMessage: (message: string) => void
}

const DEFAULT_HARD_BLOCK_SECONDS = 10

function getWebView(): WebViewBridge | undefined {
	return (window as Window & { chrome?: { webview?: WebViewBridge } }).chrome?.webview
}

function App() {
	const [isMonitoring, setIsMonitoring] = useState(true)
	const [viewMode, setViewMode] = useState<ViewMode>('settings')
	const [hardBlockSecondsRemaining, setHardBlockSecondsRemaining] = useState(DEFAULT_HARD_BLOCK_SECONDS)
	const [protectionIsRequested, setProtectionIsRequested] = useState(false)
	const [protectionSecondsRemaining, setProtectionSecondsRemaining] = useState(0)
	const [ctrlAltHeld, setCtrlAltHeld] = useState(false)

	const postToHost = useCallback((msg: string) => {
		getWebView()?.postMessage(msg)
	}, [])

	useEffect(() => {
		const webView = getWebView()
		if(!webView) {
			return
		}

		const handleMessage = (event: MessageEvent) => {
			const message = event.data as IncomingMessage | string | undefined

			if(typeof message === 'string' || !message || typeof message !== 'object') {
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
				return
			}

			if(message.type === 'protection-state') {
				setProtectionIsRequested(message.isRequested)
				setProtectionSecondsRemaining(message.secondsRemaining)
				return
			}

			if(message.type === 'ctrl-alt-state') {
				setCtrlAltHeld(message.held)
				return
			}
		}

		webView.addEventListener('message', handleMessage)
		postToHost('get-protection-state')
		return () => webView.removeEventListener('message', handleMessage)
	}, [postToHost])

	// Hard-block countdown
	useEffect(() => {
		if(viewMode !== 'hard-block' || hardBlockSecondsRemaining <= 0) return
		const timer = window.setInterval(() => {
			setHardBlockSecondsRemaining((current) => Math.max(0, current - 1))
		}, 1000)
		return () => window.clearInterval(timer)
	}, [hardBlockSecondsRemaining, viewMode])

	// Protection cooldown countdown (local decrement, syncs from host on each app-ready/get-protection-state)
	useEffect(() => {
		if(!protectionIsRequested || protectionSecondsRemaining <= 0) return
		const timer = window.setInterval(() => {
			setProtectionSecondsRemaining((current) => Math.max(0, current - 1))
		}, 1000)
		return () => window.clearInterval(timer)
	}, [protectionIsRequested, protectionSecondsRemaining])

	const handleRequestDisableProtection = useCallback(() => {
		postToHost('request-disable-protection')
	}, [postToHost])

	const handleDebugExit = useCallback(() => {
		postToHost('debug-exit')
	}, [postToHost])

	return (
		<div className="min-h-screen bg-slate-950 text-slate-200 p-8">
			<div className="max-w-2xl mx-auto">
				{viewMode === 'hard-block' ? (
					<HardBlockPage secondsRemaining={hardBlockSecondsRemaining} />
				) : (
					<SettingsPage
						isMonitoring={isMonitoring}
						onToggleMonitoring={() => setIsMonitoring(!isMonitoring)}
						protectionIsRequested={protectionIsRequested}
						protectionSecondsRemaining={protectionSecondsRemaining}
						onRequestDisableProtection={handleRequestDisableProtection}					ctrlAltHeld={ctrlAltHeld}						onDebugExit={handleDebugExit}
					/>
				)}

				<div className="text-center mt-8 text-xs text-slate-500">
					Press <span className="font-mono">F12</span> to open DevTools
				</div>
			</div>
		</div>
	)
}

export default App