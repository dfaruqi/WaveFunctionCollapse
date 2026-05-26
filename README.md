# Wave Function Collapse for 2D Tilemaps

This package provides a performant implementation of **Wave Function Collapse (WFC)** procedural generation for Unity2D along with a suite of editor tools to create and edit templates and module sets seamlessly with Unity’s Tilemap Editor. Generation uses the simple-tiled method for module creation and lowest-entropy cell selection. For larger maps generated in chunks (or, more accurately, blocks), the system uses an approach suggested by Boris the Brave called [Layered Block Evaluation](https://www.boristhebrave.com/2021/11/08/infinite-modifying-in-blocks/), which completes the map in 4 passes. Blocks in each pass are generated in parallel, and the number of threads/cores that can be utilized scales with the map size. 

Included are three ways to generate maps:
 - Wave Function Collapse: Simple - generates an n x n map. 
 - Wave Function Collapse: Chunked - generates an n x n map in blocks. For large maps, this will speed up generation significantly.
 - WfcWorldStreamer - A world streamer that generates, loads, and unloads the map around the target transform using layered block evaluation. Saves and loads from file.

![demo1](https://github.com/dfaruqi/WaveFunctionCollapse/blob/main/Gifs/wfc1.gif) ![demo2](https://github.com/dfaruqi/WaveFunctionCollapse/blob/main/Gifs/wfc2.gif)

---

## Install via Unity Package Manager

**Recommended Unity Version:** 6000.0.60f1 +

1. Open Unity and go to **Window → Package Manager**.  
2. Click the **+** button and choose **Add package from git URL…**.  
3. Enter the following URL: https://github.com/dfaruqi/WaveFunctionCollapse.git

---

## Getting Started

Try the included sample scene:  
**`Samples~/WFC Generation and Module Editing`**

1. Import and open the sample scene from Window > Package Manager > In Project > Wave Function Collapse > Samples.
2. Press **Play**.  
3. Select the **"WFC"** GameObject in the Hierarchy.  
4. In the Inspector, locate the **Wave Function Collapse** component.  
5. Adjust any parameters you’d like, then press **Generate**. The system will generate a map from the module set and apply it to the first active tilemap in the scene.

6. To watch the map generate in real time, use the **Wave Function Collapse Naive** component instead. This implementation is slow and should not be used in a production setting, but is great for visualizing lowest-entropy cell selection for demonstration.
7. To use the WfcWorldStreamer, enable the component on the **World Streamer** GameObject. To spawn GameObjects from GameObjectTiles, enable the **World Spawner** component. 


---

## Creating Custom Tilesets and Rules

### 1. Draw a New Template
Use the built-in Unity Tile Palette (Window > 2D > Tile Palette) to draw a new template (like the examples in the sample scene) on a tilemap. The template should include all adjacency allowances (up, down, left, or right) desired in the output. You can enable or disable specific tilemap grids in the hierarchy to edit different templates.

---

### 2. Create a Tile Database
A tile database is just a mapping of integer -> Tile. The WFC system will use the integer ID during generation.

1. Go to **Assets → Create → Databases → TileDatabase**.  
2. In the newly created database, click **“Overwrite from Tilemap”**.  
   - This populates the database with the tiles from the first active tilemap in the scene.

---

### 3. Create a Module Set

A module set packages everything needed for generation: tiles, rules, and weights. Databases can be swapped out to "reskin" an existing module set. 
1. Go to **Assets → Create → Wave Function Collapse → WfcModuleSet**.  
2. In the new module set:
   - Drag in the tile database you just created.
   - Create a new WfcWeights with the "new" button.
   - Create a new WfcTileRules with the "new" button.
   - Click **“Scan Tilemap and Overwrite”** again.  
     - This automatically generates adjacency rules based on what it finds in the tilemap.


Example module sets can be found in:

Runtime/Generation Templates/


---

### 4. Adjust Weights and Assign
1. Set the **weights** in the module set to your desired values.  
2. Drag and drop the module set into a **WaveFunctionCollapse** or **WfcWorldStreamer** script to use it for generation.  

### 5. Tuning and Mastering.

Wave Function Collapse is very powerful because it gives a high level of control on its output. There are some downsides to the approach:
1. You cannot guarantee that there will be no errors with a given template. Large or complex templates may have unintended failures. 
2. Predicting the output for a given template can be difficult and esoteric.

To deal with problem #1, the world streamer and chunked generation have robustness guarantees by first initializing the map to a trivial solution (all grass tiles, for example). When an error occurs, they fall back to previous generation layers. Therefore, a particularly error-prone template may yield squares of the default tile in the output. 

Large, complex, and robust tilesets are possible but require a deep understanding of the rules, and there are many techniques to create particular outputs. It is up to the user to create custom templates that are robust and beautiful, which can take practice and mastery. Tuning this system could genuinely be its own profession.