# SongRequestDesktopV2Rewrite
![test](https://badgen.net/badge/status/stable/green?icon=github)
![test2](https://badgen.net/badge/latest/v2.1/blue?icon=version)

![avast](https://i.ibb.co/pr2hn5z/Avast-Safe2.png) *
# Desktop Application for managing Song Requests

## Installation

1. Please download the latest release from the Releases Page
2. Extract the .zip File
3. Run SongRequest V2.exe and have fun!

## Troubleshooting
- **Error on the bottom right of the screen**
  
  Make sure you have an active internet connection and that your host is configured and running. You can open the settings of the Application and adjust the host URL

- **Error 401: Unauthorized**
  
  Double-Check that you have authenticated in the Application Settings

- **No Lyrics available for this video**
  
  There is nothing you can do as the song does not have YT Subtitles and is not registered in the LyricsOVH API

- **Error on Download**
  
  Either the URL was invalid, or it is other Content that is hosted as a livestream or community post, or you have reached a rate limit

## Libraries and Dependencies

- .NET Framework is **not** mandatory as all libraries are already included within the release
- YouTube Explode: Downloader and YT API Library (included)
- Newtonsoft.JSON: Json default library (included)
- CEF embedded framework: browser in app (included)

*SR Desktop has been scanned by Avast Premium Antivirus many times during development
