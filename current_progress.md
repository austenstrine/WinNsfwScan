I have NudeNet and EraX NSFW models running on python servers, and then I have some threads dedicated to the NsfwSharp model.

I have SOME logging enabled to tell the difference between them in terms of performance, but it's not totally clear, I'm going to have to make it a bit more readable.

Detection is still not at 100% - it appears that shifting the position of the screen vertically and/or horizontially results in different levels of detection, so the next step is "shifting" or "scrolling" the screenshot in order to get better detection.

I've already found a speed baseline, what I'm searching for now is an accuracy baseline. Once we have both we can work on figuring out how to acheive both baselines simultaneously.

Maybe a diverse combination of models is the answer, maybe particular divisons or mutations of the screen are the answer, I'm not sure. I just have to keep trying until I get something that covers up nsfw content with accuracy approaching 100% within a cycle <= 100ms. It's a very tall order, but one worth exploring.