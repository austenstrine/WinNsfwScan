function IsLoadedMessage() {
	// Send message to WPF that we're ready
	(window as any).chrome?.webview?.postMessage("app-ready");
	return <></>;
}

export default IsLoadedMessage;