
I want to create a mod for the game vintage story.

# Agent Directive:
Read through the documentation and code you can find online, and implement the following Vintage Story code mod. You can use the API documentation at https://apidocs.vintagestory.at/ as a starting point. Note that we want to create a mod for game version 1.21.6, which uses .NET 8.

# Functionality:
1. Using the existing archimedes screw in the game as a starting point create an archimedes screw for water transport.
    - Our water transport archimedes screw should use the exact same visual behaviour as the existing one: connecting multiple screw blocks, variant, etc.
2. This device should be placed with one end in water, and then additional custom archimedes screw blocks should be placeable on top.
3. The device should be mechanically powered, in the same way the existing one (which is purely used for item transport) is.
4. At the opposite end of the archimedes screw, at the one that is not in water, in the block above it, when the screw is powered a custom type of water source block should be created, called an archimedes-water-source. archimedes-water-source should be functionally and visually identical to standard water source blocks, but their purpose will be detailed in a little bit. While the screw is powered, the code should check for air blocks next to any archimedes-water-source blocks attached to the created one, in the horizontal axis (all 4 sides). If one or more air block exists, replace it with an archimedes-water-source block. Keep doing that until therr are no air blocks, at which point the interval should be increased (fewer checks) until an air block appears.
5. If the archimedes screw is destroyed, unpowered, or the archimedes-water-source block previously created by the screw is removed, the opposite should happen: i.e, find all archimedes-water-sourc blocks that bordered that block and replace them with air. Repeat in a cascade until all the created source blocks have been removed.
6. The archimedes screw water spread detailed above should be limited to a max radius.

# Notes:
1. The mod should work on a multiplayer server
2. Ensure to use optimized coding approaches
3. Ensure you support Config Lib at https://github.com/maltiez2/vsmod_configlib
