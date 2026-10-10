# Changelog

Each release's notes come from its section here (the heading must match the tag, for example `## v1.1.0`).

## v1.1.2

- **Android phones now work on Mac** as well as iPhones and iPads. Set the phone's USB mode to **Photo transfer (PTP)**,
  which macOS can read (it can't read the normal File transfer mode). The app now says this when you turn phone import on,
  and gives a hint if a device shows no photos.

## v1.1.1

- **You choose where photos go.** The first time the app runs it asks for the photo folder (the suggestion is
  `Pictures\Photography\Photos`), and you can change it any time with **Change…** in the window. v1.1.0 started out
  pointing at a fixed folder on the author's drive; if you installed v1.1.0, open the window and check **Photo folder**.
- The Lightroom plugin's fallback folder is now your Pictures folder too (it normally follows the app's setting anyway).

## v1.1.0

**The app now updates itself.** Once a day (and when you click **Check now**) it looks for a newer release on GitHub.
If there is one, choose **Install update** in the window: it downloads the new version, checks it against the release's
checksums, swaps it in and restarts. You can turn the daily check off with **Check for updates automatically**.
This is the only time the app uses the internet, and it only talks to GitHub. You'll need to install v1.1.0 by hand
once; after that it updates itself.

**Choose what to import.** New switches for **RAW**, **JPEG / HEIC** and **Videos**. Files you don't pick stay on the card,
and deleting from the card only ever removes files that were imported.

**Phone import (off by default).** Switch on **Import from phones** to copy from an iPhone or iPad (Windows and Mac) or an
Android phone (Windows). Each phone has to be approved the first time it connects (**Always allow**, **Just this time** or
**Don't allow**), only the camera folders are read, and nothing is ever deleted from a phone. **Forget phones** removes the
approvals. On Windows a phone shows up when it is unlocked and set to File transfer (Android) or has trusted the PC (iPhone).
Android on Mac isn't supported, because macOS can't read Android phones natively. Phone files are checked by size rather
than by hash, since a phone can't hand over the original bytes.

- The window opens centred above the tray icon, like other tray apps.
- New setting for the **Photo folder** (Windows: **Change…** in the window). The Lightroom plugin follows it.
- The switches in the window now show clearly when they're on.
- RAW formats from Canon, Nikon, Fujifilm, Olympus, Panasonic and Pentax are recognised, and HEIC.
- Lightroom plugin 1.2.0 also picks up those RAW formats and HEIC.

## v1.0.0

First release: tray / menu-bar app that copies photos and videos from camera and drone cards into date folders,
verifies every copy, and optionally clears the card, plus a Lightroom Classic plugin that adds the new files to your catalog.
