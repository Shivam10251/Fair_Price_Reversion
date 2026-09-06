from youtube_transcript_api import YouTubeTranscriptApi

video_id = "8lbkj0PF1uM"

api = YouTubeTranscriptApi()

transcript = api.fetch(video_id)

for snippet in transcript:
    print(snippet.text)