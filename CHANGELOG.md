# Changes

The app shows the section for the new version when an update is available. Keep each line short.

## v1.3.0

- New: "Open all profiles with the extension" button in the Profiles card opens them in one click
- Can also open the site in each one (tick "Also open the site in each one")

## v1.2.3

- Faster replies: going back to the chat list and closing the message panel no longer wait out fixed pauses
- The log shows how long each part of a chat took, to find what's still slow

## v1.2.2

- Fix "chat did not open": works with the site's new message box and Send button
- The extension updates itself (no need to reload it)

## v1.2.1

- Fix: Multitask mode fell back to Focus mode (and maximized windows) when more than one profile was running

## v1.2.0

- Autopilot has two modes: Focus (as before) and Multitask (new, testing)
- Multitask mode works in the background, so you can keep typing in other windows
- In Multitask mode, a profile that isn't ready uses Focus mode for that step (shown in the log)

## v1.1.0

- Updates install from inside the app: press Update now and it restarts by itself
- The helper extension reloads itself after an update (no more chrome://extensions)
- New look: steps 1-2-3, settings grouped in cards, run bar and log always in view
- Autopilot mode (was Chat & reply); two tasks are hidden for now
- After a Telegram reply, other new chats on that profile are still answered
- Telegram alert when the site says you lost your connection or it's updating
- Chats left unread are checked again instead of being skipped
