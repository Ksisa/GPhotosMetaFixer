# Google Photos Metadata Fixer - Requirements

## Problem Description

I just used Google Takeout to download all of my Google Photos. But there are a lot of problems with the metadata. I need to fix it before importing it into immich.

Photos and videos that were taken with the phone itself seem mostly fine. They have the right timestamp and geodata. But anything else is completely fucked. Photos or videos downloaded from chats, social media, uploaded from the computer or another device, screenshots - all of the files have wrong metadata. I need some kind of script that will go through each image and their metadata file and make them consistent. I tried doing this in a powershell script, but there are so so so many edge cases that it's just too complicated for AI, it appears this is going to be a multi-step process. I also want to open source this solution for anyone else in my situation

## File Structure

All photos and videos come with a supplemental metadata json file
So for photo
PXL_1234.jpg
there will be
PXL_1234.jpg.supplemental-metadata.json

## Filename Truncation Issues

There is a name file limit to the .json file which complicates a lot of things. It will truncate the name, so
Image: PXL_20221219_115426964.jpg
will become
Metadata: PXL_20221219_115426964.jpg.supplemental-metada.json

In the most extreme case it will truncate even the name of the file itself.
Image: PXL_20221219_114459254.TS_exported_13241_167156.jpg
Metadata: PXL_20221219_114459254.TS_exported_13241_16715.json

## Requirements

I want this app to read all of the files in a directory recursively:

1. **Match each media file with a .json file** - Handle the filename truncation issues

2. **Set date taken to the right one**
   2.1 If the photo/video taken metadata entry is within 2h of "photoTakenTime" in the json file, leave it as is, otherwise replace it.
   2.1.1 Prefer "photoTakenTime", use "creationTime" as fallback, log if this happens
   2.2 Log warning if both the image metadata and the json metadata put the timestamp in the last week, don't change the timestamp.

3. **Geolocation handling**
   3.1 If there is no geodata on the image/video, AND geodata are not 0's in the metadata file, update the image/video with the geodata.

## Additional Notes

- This should be written into a file that can be read in other chats
- The solution should be open source for others in similar situations
- Handle all the edge cases that make PowerShell scripting too complex
- Multi-step process approach

