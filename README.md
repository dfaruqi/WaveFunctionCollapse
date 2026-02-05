# Wave Function Collapse for 2D Tilemaps

This package provides a highly performant implementation of **Wave Function Collapse (WFC)** procedural generation in 2D and a suite of editor tools to create and edit module sets seamlessly with Unity’s Tilemap Editor, optimized with Unity's jobs system and the burst compiler. Generation uses the simple-tiled method for module creation and lowest-entropy cell selection. For larger maps generated in chunks, the system uses an approach similar to [modifying-in-blocks](https://www.boristhebrave.com/2021/11/08/infinite-modifying-in-blocks/), but instead of generating only one block at a time, generates nonadjacent blocks at the same time on different worker threads, completing the map in 4 parallelized passes.

![Demo](gifs/wfc1.gif) ![Demo](gifs/wfc2.gif)

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


---

## Creating Custom Tilesets and Modules

### 1. Draw a New Template
Use the built-in Unity Tile Palette (Window > 2D > Tile Palette) to draw a new template (like the examples in the sample scene) on a tilemap. The template should include all adjacency allowances (up, down, left, or right) desired in the output. You can enable or disable specific tilemap grids in the hierarchy to edit different templates.

---

### 2. Create a Tile Database
A tile database is just a mapping of integer -> Tile and is the basis for all WFC operations.

1. Go to **Assets → Create → Databases → TileDatabase**.  
2. In the newly created database, click **“Overwrite from Tilemap”**.  
   - This populates the database with the tiles from the first active tilemap in the scene.

---

### 3. Create a Module Set

A module set depends on a Tile Database and represents the allowed neighbors and weights of any subset of tiles from that database. Databases can be swapped out to "reskin" an existing module set. 
1. Go to **Assets → Create → Wave Function Collapse → WfcModuleSet**.  
2. In the new module set:
   - Drag in the tile database you just created.  
   - Click **“Scan Tilemap and Overwrite”** again.  
     - This automatically generates adjacency rules based on the template.

---

### 4. Adjust Weights and Assign
1. Set the **weights** in the module set to your desired values.  
2. Drag and drop the module set into a **WaveFunctionCollapse** script to use it for generation.  

Example module sets can be found in:  

Runtime/Modules/