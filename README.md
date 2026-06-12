# SprayMod - Client & Server-Side Spray System

A comprehensive spray mod for Puck that supports both **client-side only** and **server-side spray sharing** modes.

## Features

### Client-Side Mode (Default)
- Place custom spray images on surfaces using the `/spray` command
- Visual spray wheel UI for spray selection
- Support for PNG, JPG, and animated GIF images
- Async texture loading for smooth performance
- Default spray images included

### Server-Side Mode (Multiplayer)
- **All players see each other's sprays** when server-side sharing is enabled
- Server authoritative spray placement with validation
- Configurable spray limits, cooldowns, and lifetimes
- Automatic spray cleanup and management
- Optional spray persistence between rounds

## Installation

### Workshop Installation (Recommended)
1. Subscribe to SprayMod on Steam Workshop
2. The mod will install to: `Puck_Data/StreamingAssets/Mods/SprayMod.dll`
3. Default spray images and sound will be in: `Puck/Sprays/`

### Manual Installation
1. Place `SprayMod.dll` in your Puck mods folder
2. Copy the `Sprays` folder to your Puck root directory
3. Launch Puck and enable the mod

## Usage

### Basic Commands
- **`/spray`** - Opens the spray wheel to select and place a spray
- Look at a surface and use the spray wheel to place your spray
- Press **ESC** to close the spray wheel

### Spray Wheel Controls
- **Open Folder** - Opens the spray images folder to add custom sprays
- **Clear All** - Removes all your sprays from the map
- **Close** - Closes the spray wheel

## Configuration

### Server Configuration
Servers can configure spray behavior via `config/spray_config.json`:

```json
{
  "EnableSpraySharing": true,        // Enable server-side spray sharing
  "MaxSpraysPerPlayer": 10,          // Maximum sprays per player (0 = unlimited)
  "MaxTotalSprays": 100,             // Maximum total sprays on server (0 = unlimited)
  "SprayLifetime": 0,                // Spray lifetime in seconds (0 = permanent)
  "PersistBetweenRounds": false,     // Keep sprays between rounds
  "SprayCooldown": 1.0,              // Minimum time between sprays (seconds)
  "EnableSpraySound": true,          // Enable spray sound effects
  "AllowCustomSprays": true,         // Allow clients to use custom images
  "MaxSprayImageSize": 512,          // Maximum spray image size in KB
  "DebugMode": false                 // Enable debug logging
}
```

#### Server Config Options

- **EnableSpraySharing**: When `true`, all players see each other's sprays (server-side mode). When `false`, sprays are client-side only.
- **MaxSpraysPerPlayer**: Limit how many sprays each player can have active at once. Oldest sprays are automatically removed.
- **MaxTotalSprays**: Total spray limit across all players. Prevents server performance issues.
- **SprayLifetime**: How long sprays stay on the map (0 = permanent until limit reached).
- **PersistBetweenRounds**: Keep sprays when the round ends/restarts.
- **SprayCooldown**: Prevent spray spam with a cooldown timer.
- **EnableSpraySound**: Server can disable spray sounds for all players.
- **AllowCustomSprays**: Server can restrict players to default sprays only.
- **MaxSprayImageSize**: Maximum file size for custom spray images.
- **DebugMode**: Enable detailed logging for troubleshooting.

### Client Configuration
Players can configure their spray experience via `config/spray_client_config.json`:

```json
{
  "Debug": false,                    // Enable debug logging
  "ShowUI": true,                    // Show spray wheel UI
  "EnableSound": true,               // Enable spray sounds
  "ShowOtherPlayerSprays": true,     // Show other players' sprays
  "SprayOpacity": 1.0                // Spray opacity (0.0 - 1.0)
}
```

#### Client Config Options

- **ShowUI**: Hide the spray wheel UI if you prefer
- **EnableSound**: Disable spray sounds locally
- **ShowOtherPlayerSprays**: Hide other players' sprays (only see your own)
- **SprayOpacity**: Adjust transparency of sprays (1.0 = opaque, 0.0 = invisible)

## Custom Spray Images

### Adding Your Own Sprays

1. Click **"Open Folder"** button in the spray wheel, OR
2. Navigate to `Puck/Sprays/SprayImages/`
3. Add your custom images (PNG, JPG, or GIF)
4. Reload the game or use `/spray` again to load new images

### Image Recommendations
- **Format**: PNG with transparency, JPG, or GIF
- **Size**: 256x256 to 512x512 pixels
- **File Size**: Under 512 KB (configurable by server)
- **GIF Animations**: Fully supported with all frames

## How It Works

SprayMod is **fully client-side** — there is no server component and no server-side mod is
required. It works on any vanilla server because sprays are shared peer-to-peer over the game's
own chat channel.

### Placing a spray
1. Open the spray wheel (default `Z`, or `/spray`) and click a spray
2. The spray is placed on the surface you're looking at (a screen-space decal that conforms to
   the geometry, or the classic flat quad if you toggle Flat Sprays)
3. If sharing is enabled, the placement is broadcast to everyone else running the mod

### Peer-to-peer sync (over chat)
- A placement is encoded into a tiny message — `Base64(position + normal + scale + roll)` plus
  either a short image URL or a content hash — and sent as a chat message
  (see [SprayChatSync.cs](src/SprayChatSync.cs))
- Every client receives it in `ChatManager.AddChatMessage`; clients **with** the mod read the raw
  content first, decode it, hide it from the chat UI, and render the spray
- The message is **invisible to players without the mod**: it's encoded with Unicode Tag
  characters, which a vanilla client's chat display strips to nothing and hides entirely — so
  non-mod players never see chat spam
- Image sprays are auto-hosted to a short link so everyone can fetch them; hash sprays fall back
  to a placeholder for receivers who don't already have that image
- Orientation (roll around the surface normal) is synced, so a spray looks the same for everyone

## Folder Structure

```
Puck/
├── Puck_Data/
│   └── StreamingAssets/
│       └── Mods/
│           └── SprayMod.dll
├── config/
│   ├── spray_config.json          (server config)
│   └── spray_client_config.json   (client config)
└── Sprays/
    ├── SpraySound.wav              (spray placement sound)
    └── SprayImages/                (custom spray images)
        ├── default-spray-1.gif
        ├── default-spray-2.gif
        └── [your custom images]
```

## Troubleshooting

### Sprays Not Showing for Other Players
- Check if server has `EnableSpraySharing: true` in `spray_config.json`
- Verify players have `ShowOtherPlayerSprays: true` in `spray_client_config.json`
- Ensure all players are using the mod (server-side spray sharing requires mod on all clients)

### Custom Sprays Not Loading
- Check that images are in `Puck/Sprays/SprayImages/` folder
- Verify image format (PNG, JPG, or GIF)
- Check file size is under server's `MaxSprayImageSize` limit
- Look for errors in `Puck/Logs/` folder

### Performance Issues
- Reduce number of spray images in folder
- Lower spray limits in server config
- Set `SprayLifetime` to automatically clean up old sprays
- Disable GIF animations (use static PNG/JPG instead)

### Config Not Loading
- Check that config files are in `Puck/config/` folder (not `Puck_Data/config/`)
- Verify JSON syntax is valid (use a JSON validator)
- Check file permissions (read/write access)
- Enable `DebugMode: true` to see detailed loading logs

## Version History

### 1.0.0 (Current)
- Initial release with client-side spray placement
- Chat command integration (`/spray`)
- Visual spray wheel UI
- Async texture loading for performance
- Full GIF animation support
- Workshop integration with default spray images

### 2.0.0 (This Update)
- **NEW**: Server-side spray sharing (all players see sprays)
- **NEW**: Server and client configuration system
- **NEW**: Network synchronization with validation
- **NEW**: Configurable spray limits, cooldowns, and lifetimes
- **NEW**: Organized folder structure (Sprays/SprayImages/)
- Improved backwards compatibility (works client-side only if server config disabled)
- Enhanced performance with config-based settings

## Credits

Created by Amikiir for the Puck community.

Special thanks to:
- Puck developers for the mod API
- Community for feedback and testing
- Inspired by classic spray systems from Source engine games

## License

This mod is provided as-is for the Puck community. Feel free to modify for personal use.
