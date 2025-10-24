# Wave Function Collapse

## Install via Unity Package Manager

**Recommended Unity Version:** 6000.0.60f1 +

1. Open Unity and go to **Window → Package Manager**.  
2. Click the **+** button and choose **Add package from git URL…**.  
3. Enter the following URL:  

This package provides an implementation of **Wave Function Collapse (WFC)** procedural generation and a suite of **editor tools** to create and edit module sets seamlessly with Unity’s Tilemap Editor.


---

## Getting Started

Try the included sample scene:  
**`Samples~/WFC Generation and Module Editing`**

1. Import and open the sample scene from Window > Package Manager > In Project > Wave Function Collapse > Samples.
2. Press **Play**.  
3. Select the **"WFC"** GameObject in the Hierarchy.  
4. In the Inspector, locate the **Wave Function Collapse** component.  
5. Adjust any parameters you’d like, then press **Generate**.  

The system will dynamically draw tiles as the generation process progresses.

---

## Creating Custom Tilesets and Modules

### 1. Draw a New Template
Use the built-in Unity Tile Palette to draw a new template (like the examples in the sample scene) on a tilemap.  
You can enable or disable specific tilemap grids in the Hierarchy to edit different templates.

---

### 2. Create a Tile Database
1. Go to **Assets → Create → Databases → TileDatabase**.  
2. In the newly created database, click **“Overwrite from Tilemap”**.  
   - This populates the database with the tiles from the first active tilemap in the scene.

---

### 3. Create a Module Set
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