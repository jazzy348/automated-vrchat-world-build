# Automated VRChat World Builder
A proof of concept C# script to automate building and uploading worlds to VRChat


This is something I wrote VERY quickly to aid in the development of the Miku Experience worlds. We have multiple artists working on our worlds and having a single upload point to ensure everyone is working from the same world ID is crucial.

In our workflow I have a webhook from our Git repository which triggers the build process. I also have written a Discord bot which acts as our logs and allows us to trigger builds for specific commits or push to our production world ID.


Place this script into your Unity project then open Unity via CLI with the following:

"${Path/To/Unity.exe}" -projectPath /path/to/project/ -executeMethod AutoVRCUploader.UploadWorldCLI -- --scene=Assets/Scenes/main.unity --thumbnail=Assets/Editor/thumbnail.png --name="World Name" --id="VRC World ID" -logFile /path/to/log/file
