**NPC Plugin Chooser 2**


<img width="1000" height="1000" alt="SplashScreenImage" src="https://github.com/user-attachments/assets/9f9706ec-6230-4159-b26f-11f7aeca8f8a" />


# Description

This utility allows you to manage which mod provides the appearance of each NPC in the game. You can use it to easily mix-and-match appearance mods, avoiding face conflicts / dark face bugs.


## Why Does This Exist?

Let’s get this out of the way: Yes, I know [EasyNPC](https://www.nexusmods.com/skyrimspecialedition/mods/52313) exists. A bit of backstory: Back in 2021 I created the original [NPC Plugin Chooser](https://www.nexusmods.com/skyrimspecialedition/mods/49066), which was a Synthesis patcher or standalone command line program. N.P.C. was, for a time, included in [Lexy’s LotD](https://lexyslotd.com/), which I’m still very proud of.  Then, just a few months later, EasyNPC came out and its UI blew N.P.C. out of the water, so I let the project phase out into peaceful eternity… or so I thought.

In early 2025, I was reminiscing about the early days so I went back to look at the N.P.C. Nexus page and found something horrifying in the comments: My instructions had given someone an aneurysm. Even worse, another user chimed in and said they had one as well! I’ve always wanted to be helpful to the community and yet here I was, apparently laying ticking time bombs for unsuspecting users to stumble onto. I felt like I needed to make it right. There had always been a few features that I wished EasyNPC had (see below), but I always figured it’d be too much work to make a whole app just for those features. Fortuitously, right at that moment, someone on Discord was trying to convince me that LLMs could already make entire apps from scratch, so I figured this would be a perfect opportunity to test that claim (more on this below as well). And so N.P.C.2 was born. 


## Do I Need This?

If you’re already using EasyNPC and you’re happy with it, there’s not really any reason to switch. EasyNPC’s developer is much more experienced and meticulous than I am and you’re probably less likely to run into issues with it. But if you’re still looking to put together your NPC list, you might want to take advantage of some of the features here. Note that if you enjoy my UI but want to take advantage of EasyNPC’s well-tested code, N.P.C.2 has buttons to export to (and import from) EasyNPC.


## What Are The Features?



* Lets you select the source of each NPC’s appearance (just like EasyNPC and N.P.C.1)
* Lets you use a differen't NPC's appearance (just like my NPC Appearance Copier Synthesis Patcher)
* More flexible mod selection UI
    * Gallery view for NPC mugshots
    * You’re allowed to select mods even if you don’t currently have them installed - you can make your selections based on mugshots, and then download only those mods that you ended up choosing. 
    * Short (hopefully spoiler-free) description (base game NPCs only) to help you choose an appearance if you don’t know who they are. [Optional]
    * Quick full screen mugshot preview
    * Quick comparison button to compare a subset of available mugshots side-by-side
    * Drag-and-drop functionality to apply mugshots to a mod if their names don’t match
    * Drag-and-drop functionality to replace ModA with ModB wherever an NPC can choose from both
    * “Choose this mod for all NPCs it provides” button
    * Mod menu where you can see the mugshots of all NPCs within the mod
* Non-appearance override handling
    * If an NPC mod overrides records that aren’t typically associated with Appearance Replacers, such as Races, N.P.C.2 can handle that. 
* Not-In-Load-Order Mod Sources
    * I personally hate, hate, hate enabling and disabling plugins in my mod manager just for patching. So, NPC doesn’t require mods to be active in your load order. In fact, they don’t even have to be enabled at all. In fact, they don’t even have to be in the same mod manager installation. You can launch N.P.C.2 from C:\LoreRim, and tell it your mods folder is in C:\MO2InstallationForAppearanceMods\mods, and use those appearance mods to patch your LoreRim install.
* Create Mode
    * N.P.C.2 offers two modes: “Create”, or “Create And Patch”. “Create And Patch” is like EasyNPC: it patches your load order to your selected appearance mods. “Create” is how I used to like doing things: it ignores your load order and just splices together the mods you suggested exactly as they are. The idea is you treat the generated output just like you would any other appearance mod: put it up high in your plugin load order, low in your asset order, and then use something like Synthesis FaceFixer to forward the NPC appearances into your mod list. 
* SkyPatcher Mode
    * SkyPatcher is all the rage these days. I personally don’t think it’s a great idea to use for appearance mods because then you have to make exclusions for things like RSV or SynthEBD, but I know people would ask for it so I went ahead and included it. With SkyPatcher mode, N.P.C.2 applies its selections using SkyPatcher instead of modifying the NPC records (duh).
* Group Splitting
    * You can assign NPCs to groups and only generate output for those groups. This can be useful if you get Too Many Masters errors and don’t want to switch to SkyPatcher mode.
* Bat File Generation
    * You can quickly generate bat files to spawn all NPCs in your selected Group. This can be useful if you want to check and make sure they all look ok. 


# Installation

N.P.C.2 is theoretically compatible with both MO2 and Vortex, but I’ve only ever tested with MO2.

Install like you would any other similar utility such as EasyNPC. Extract the contents of the zip file to the location of your choice. Add the .exe as an executable in your mod manager, and run it from your mod manager.


## Basic Usage



1. Launch the .exe
2. Set the Skyrim version that you’re patching from the dropdown
3. If you’re using a non-standard data directory (e.g. RootBuilder or Stock Game), set its data folder as the Game Data Path.
4. Select your mods folder and wait for analysis to complete (this takes longer the first time you select the mods folder than in subsequent launches)
    * If MO2, this will be your mo2\mods folder
    * If Vortex, this will be your staging folder
    * The mods folder can be, but does not have to be, the same one that’s used by the mod manager you’re using to launch the .exe. It should be the one to which your appearance mods are installed. This can be a completely separate MO2 or Vortex directory if you so choose.
5. Select your Mugshots folder and wait for the mugshots to be linked to your mods.
6. Select your output folder
    * If you just type in a name, you’ll get that folder created as a subfolder within the mods folder from step 2
    * If you want to specify a folder in a different mod manager, type in the full path (or browse & select it)
7. Switch to the NPCs tab and make your selections.
    * To select from an existing option, just click the Mugshot or Placeholder for your desired appearance mod. The border will turn green if the selection is valid.
    * To apply the appearance from a different NPC:
    	* navigate to that NPC
     	* right click on the Mugshot or Placeholder corresponding to the appearance you want to use
        * select the "Share With NPC" option in the context menu
        * in the window that appears, search for the NPC you want to apply the appearance to
        * Click [Share and Select] to apply the appearance, or simply [Share] to make the appearance available to that NPC without actually selecting it yet
9. Switch to the Run tab & run the patcher
10. Enable the resulting plugin and assets in your mod manager. Make sure the assets overwrite all conflicts.


# Detailed Usage


## Setting Menu

When you first start the patcher (remember: through your Mod Manager), you’ll see the Settings Menu. This menu consists of several parts:


### Game Environment Settings



<img width="2206" height="636" alt="01 Game Environment" src="https://github.com/user-attachments/assets/cfab50bb-3957-4d63-90b0-7bbfb36bc8f7" />



Here you’ll find the settings that tell the program how your Skyrim installation is set up. If you just have a default configuration where Skyrim/SKSE are in your Steam game folder, you don’t need to change anything (Skyrim VR players, select SkyrimVR in the dropdown). If your game is in a non-standard location, you can point the environment to your data folder using the Browse button.


#### Example: Stock Game (LoreRim)



<img width="2180" height="496" alt="02 Stock Game" src="https://github.com/user-attachments/assets/21f1dff8-fe0a-4f95-87d8-c7471587d021" />




### Mod Environment Settings

Here you’ll tell N.P.C.2 where to find your appearance mods and mugshots. Your appearance mods can be in the same Mods folder as the one used by your mod manager, but it doesn’t have to be. You can use a completely different mod directory if you want (but be warned that in that case, N.P.C.2 won’t see any appearance mods in your mod manager’s mod directory). The Mugshots folder looks exactly like it would for EasyNPC:



<img width="1814" height="954" alt="03 Mugshots Folder" src="https://github.com/user-attachments/assets/046c707a-5136-40c1-9594-b7f0e61639d4" />



#### Example Setup (Skyrim NPC Selection is the name of my MO2 folder)



<img width="2164" height="384" alt="04 Same Mods Folder" src="https://github.com/user-attachments/assets/c02cba9d-8db6-4c07-8d3d-e0257c8d80e8" />





### Output Settings

Here you’ll tell N.P.C.2 where to put the files that it generates. 



<img width="2176" height="692" alt="05 Output Settings" src="https://github.com/user-attachments/assets/c649c4a3-32d7-4ca6-a686-f8770bd31371" />



**Output Directory**: The folder that it’ll create when it generates its output plugin and other resources. This can be a simple folder name, or a full path.



* If you provide a simple folder name, that folder will be generated in your Mods Folder.
* If you provide a full path, output will go to your specified folder.

Use the second option if your selected Mods Folder is not the same as your mod manager’s Mods folder.

**Append Data/Time Stamp**: If checked, the selected Output Directory will have a timestamp in its name, preventing you from overwriting a previous output. You’ll have a timestamp even if you specified a full path for your Output Directory (a new folder will be created with the same name but the time stamp added).

**Output Plugin Name**: The name that the generated plugin will get. For example, “NPC” will produce NPC.esp.

**Patching Mode**: Controls the main behavior of the patcher. There are two options here:



* *Create and Patch*: Behaves like EasyNPC. Your conflict-winning NPC records will be patched to use the appearance of your selected appearance mod. No further action on your part should be required.
* *Create*: Generates an appearance mod that you should treat like any other appearance mod that you’d download. It splices together your selected NPC appearances into a cohesive mod, but doesn’t take your own mod setup into account, so you’d have to perform conflict resolution yourself. The best workflow is to take the generated assets (e.g. left pane in MO2) and put it at the bottom of your mod list, and take the generated plugin (e.g. right pane in MO2) and put it as high as it will go. Then either manually perform conflict resolution, or use the FaceFixer Synthesis Patcher to do it automatically.

**Override Handling Mode**: Controls how the patcher behaves if it encounters an appearance mod that overrides (modifies) records (other than NPC records) - for example, an appearance mod that changes a vanilla Race. Note that this setting is overrideable per-mod in the Mods menu. There are three options here:



* *Ignore*: The patcher will not look over overridden records. If there are any overrides that require the appearance mod as a master, the output plugin will inherit this master.
* *Include*: The patcher will incorporate the changes into its output
    * In Create and Patch mode, it will attempt to delta patch the changes into the winning override in your load order
    * In Create mode, it will simply include the overridden record in its output plugin
* *Include As New*: Rather than creating an override, the patcher will copy the modified record as a new record, and point the NPC at this new record. This is useful if you don’t trust the delta patching in Include mode, or if you have two Appearance Mods that make conflicting edits to the same overridden record.

**<span style="text-decoration:underline;">Important Note</span>: Override handling is extremely slow, and 99% of appearance mods don’t need it. You’ll want to leave this setting on “Ignore” in the Settings menu, and only turn it on for those mods that need it in the Mods menu.**

**SkyPatcher Mode**: If checked, N.P.C.2 will not modify NPC records directly, but will write a SkyPatcher .ini file to apply appearance changes. Note that if override handling is engaged (specifically *Include *mode), those overrides will still be included in the output plugin. 



* Author note: I included SkyPatcher mode because I’m sure people will want it, but I don’t necessarily recommend it. It may conflict with other runtime appearance patchers, such as R.S.V. or my own SynthEBD patcher.


#### Example: Output Folder Setup (Mods Folder is my MO2\mods directory)



<img width="1046" height="778" alt="06 Output Folder Setup" src="https://github.com/user-attachments/assets/4eb4d050-7762-485a-a2a3-243ca4387f6c" />



Output will go to S:\Skyrim NPC Selection\mods\NPC Output


#### Example: Output Folder Setup (Mods Folder is NOT my MO2\mods directory)


<img width="1150" height="868" alt="07 Output Folder Setup - LoreRim" src="https://github.com/user-attachments/assets/0cfc9bd7-2887-4c58-a557-e9c273d444d9" />




Output will got to C:\Games\Skyrim AE\LoreRim\mods\NPC Output even though that’s not where I’m sourcing the NPC appearance mods from


### Display Settings

Here you’ll find settings pertaining to how N.P.C.2 looks and renders.



<img width="600" height="198" alt="08 Display Settings" src="https://github.com/user-attachments/assets/62618534-2bc6-4260-b003-6463ede3b03c" />



**Normalize Image Dimensions**: If checked, N.P.C.2 will show each MugShot with the same size and aspect ratio (cropping to center if necessary). If unchecked, images will be shown at their raw resolution (not as visually appealing, but may be what you prefer).

**Max # Mugshots to Fit**: Whenever you select an NPC or Mod, N.P.C.2 will try to fit up to this number of mugshots on the screen (shrining them until they all fit). The higher this number, the smaller the images will be (and the longer they’ll take to load). Note that you can always zoom in/out after the images load in.


### EasyNPC Transfer



<img width="2184" height="822" alt="09 EasyNPC Transfer" src="https://github.com/user-attachments/assets/007c5bc2-3aa9-4df7-8396-ddaf2992ed4b" />



**Import NPC Apparance Choices from EasyNPC Profile**: Select your exported EasyNPC Profile to import you selections from it. You can generate this profile using the button in the screenshot below. Note that EasyNPC selects Plugins, while N.P.C.2 selects Mods. The import will attempt to match EasyNPC’s plugin selection to your available mod list. 



<img width="2184" height="822" alt="09 EasyNPC Transfer" src="https://github.com/user-attachments/assets/514d6105-a079-4769-93ce-7725c110ab94" />



**Export NPC Appearance Choices to New EasyNPC Profile**: Converts your mod selection list into a plugin profile that EasyNPC can import using the button in the screenshot below. EasyNPC Default Plugins are chosen based on your conflict winning records. Note that facegen-only mods won’t be exported because they have no corresponding plugin.



<img width="1360" height="806" alt="11 EasyNPC Import" src="https://github.com/user-attachments/assets/a8b7793f-cd54-44fe-8d05-241c562928bc" />



**Update Existing EasyNPC Profile**: Similar to the above, except it keeps the Default Plugin selection from the selected EasyNPC profile and only changes the Appearance Plugin



* **Add Missing NPCs**: If checked, NPCs that you’ve made selections for that don’t exist in your selected EasyNPC profile will be added

**NPC Default Plugin Exclusions**: When exporting an EasyNPC profile, the plugins you select here will be ineligible to be chosen as Default Plugins, and N.P.C.2 will look for the next-available override. Useful if you don’t want to use Synthesis/zEdit outputs as Default Plugins.


### Load Order Import Settings

Here you’ll find settings that modify what happens when you import NPC selections from your load order (you can do this in the **NPCs Menu**).



<img width="2186" height="620" alt="12 Load Order Import" src="https://github.com/user-attachments/assets/e6e88545-4d37-41b2-9e99-ce9547a44b6a" />



**Import Choices From Load Order Exclusions**: Any plugins that you select here will be skipped when importing your winning appearance mods from your current load order (and the next-winning override will be chosen instead).


### Mod Import Settings

Here you’ll find settings pertaining to N.P.C.2’s analysis of your mod list



<img width="2182" height="588" alt="13 Mod Import" src="https://github.com/user-attachments/assets/ce80cb99-3000-4f5e-b3db-489998545871" />



**Non-Appearance Mods**: Here you’ll find mods that N.P.C.2 has classified as not providing a new NPC or modifying any NPC appearances. If you think N.P.C.2 has made a mistake, or if the mod updates and starts providing NPCs, click the red X to have N.P.C.2 re-analyze it on the next start up.

Spawn Bat File Options

Here you’ll find options for the .bat files that N.P.C.2 can create (in the **Run** menu)



<img width="2182" height="578" alt="14 Spawn Bat" src="https://github.com/user-attachments/assets/ce3d9e76-a8e9-4a19-a2b7-7ac42483f9c2" />



**Console Commands Before Spawning**: Here you can add any commands you want to include in the .bat file before the “player.placeatme” lines

**Console Commands After Spawning: **Here you can add any commands you want to include in the .bat file after the “player.placeatme” lines. Author note: I like to add the tai command here so the NPCs don’t wander off before I have a chance to inspect them.


## NPCs Menu

The NPCs Menu is where you’ll make your appearance mod selections. It has a few options to tweak display options to your liking, make comparing appearance replacers easier, and perform some advanced functionalities.




<img width="2214" height="1196" alt="15 NPCs Menu" src="https://github.com/user-attachments/assets/31572f2a-2560-4631-9187-4bd4d8183a44" />


**Left Panel**: Select the NPC whose appearance you want to choose in the left panel. 



* Right clicking on an NPC will bring up a context menu allowing you to choose whether or not to forward its outfit into the patcher output.

    


<img width="1066" height="510" alt="16 NPCs Left Panel Context" src="https://github.com/user-attachments/assets/44841c43-3c5b-45ff-88ca-62ee105a6718" />



**Right Panel**: Select the appearance replacer you want to use in the right panel. 



* The border will become outlined in green if you have the mod installed, or in purple if you’ve selected a mugshot for which the corresponding mod is not installed (in which case you can still run the patcher, but that NPC will be skipped until you install the corresponding mod)
* NPCs from appearance mods for which there’s no mugshot will receive a placeholder image
* You may see one or more symbols in the corner(s) of the mugshots. These will be explained below.

**NPC Groups**: Here you can define Groups to add NPCs to. When patching (or creating .bat files), you can choose to operate on all NPCs, or only those included in a Group. To create a new group, click into the box and type the group name. To use a previously-made group, select it from the dropdown. Once the group is selected, you may use one of the options to the right:



* **Add Cur**rent NPC: Adds the currently selected NPC to the selected group
* **Remove Cur**rent NPC: Removes the currently selected NPC from the selected group
* **Add Vis**ible NPCs: Adds all NPCs that are visible in the left panel with your current filter selections to the selected group.
* **Remove Vis**ible NPCs: Removes all NPCs that are visible in the left panel with your current filter selections from the selected group.

**Show Single Options NPCs**: Controls the visibility of NPCs for whom there’s only a single Appearance Mod (the one where the NPC is defined). You may not want to see these, since there’s nothing to choose between for these NPCs.

**Show Unloaded NPCs**: Controls the visibility of NPCs that are not currently in your load order. These can be NPCs for which you’ve downloaded a mugshot pack, or (if you’re using an Appearance Mods folder that’s not the same as your mod manager’s Mods folder) NPCs that are defined in plugins in your Appearance Mods folder, but are not in your current load order (you might want to see these in order to copy their appearance to an NPC that **is** in your load order).

**Show Hidden Mods**: Controls the visibility of mugshots from mods that you’ve hidden using the right click context menu (see below).

**Show NPC Descriptions**: Controls the visibility of NPC descriptions. These are short summaries of who an NPC is sourced from either UESP or the Elder Scrolls Fandom Wiki. These can be helpful in determining which appearance to assign to an NPC if you can’t remember who they are and what (lore-wise) they should look like. N.P.C.2 needs to connect to the internet for this feature. For NPCs that aren’t in the vanilla game, you can provide (and share with others) descriptions in a .json format. These would go in {base directory}\DescriptionOverrides. See ExampleOverride.json for how to add descriptions.

**Get Choices From Load Order**: Scans your load order to determine which mods you’re currently using as appearance mods, and selects those mods for your NPCs.

**Export My Choices**: Backs up your current appearance mod selections to a .json file (note: unlike the export from the Settings Menu’s EasyNPC section, this file is not cross-compatible with EasyNPC).

**Import My Choices**: Loads your selections from the backup file generated via the **Export My Choices** button.

**Clear My Choices**: Clears all of your appearance mod selections, allowing you to start from scratch.

**Compare Selected**: Using the checkboxes in the top-right corner of each appearance mod, you can select two or more to create head-to-head comparison in full screen, ignoring the ones you don’t find interesting. 



<img width="2212" height="966" alt="17 Compare Selected" src="https://github.com/user-attachments/assets/d09a2286-681f-4ee4-a2a1-1158dd3b91c2" />


<img width="2210" height="1246" alt="18 Compare Selected" src="https://github.com/user-attachments/assets/0a482181-a88c-404e-8fb6-1f249275a2fc" />


**Hide/Unhide**: If you don’t want to see mugshot(s) for a given NPC, you can place a checkmark in their top-right corner and click this button to hide them. If you change your mind later, check the **Show Hidden Mods** box at the top, select the hidden mugshots, and click **Hide/Unhide** again to restore them.

**Deselect All**: Quickly removes all checkmarks from all mugshots.

**Appearance Filters**: Here you can search for specific NPC(s) to display in the left panel, using:



* Name: The name of the NPC
* EditorID: The EditorID of the NPC
* In Appearance Mod: The name of an appearance mod that provides these NPC(s) 
* Chosen In Mod: The name of an appearance mod which you’ve selected for these NPC(s)
* From Plugin: The name of the plugin in which the NPC(s) are defined
* FormKey: The FormKey of the NPC (if you don’t know what this is, don’t worry about it)
* Selection State: Whether or not you’ve assigned an appearance to the NPC(s)
* Group: NPCs belonging to a given group

**Zoom Controls**: Zoom controls are at the bottom. When selecting an NPC, the program will try to fit all of the mugshots on screen (up until it reaches the **Max # Mugshots to Fit** setting from the Settings Menu). From there you can zoom in/out. 

**Lock Zoom**: If you want all mugshots for all NPCs to be of a certain size, zoom in/out to that size and then click this button.

**Reset Zoom**: Resets the zoom to again try to fit all mugshots (until **Max # Mugshots to Fit**) on screen.

**Right Click Context Menu**




<img width="1460" height="1048" alt="19 Mugshot context" src="https://github.com/user-attachments/assets/b3fc367f-26fd-4374-a9a0-d19dfda64c55" />




* Select: Selects the appearance mod (same as just left clicking on it)
* Hide: Hides the mugshot (same as the **Hide/Unhide** button)
* Unhide: Unhides the mugshot (same as the **Hide/Unhide** button)
* Select All From This Mod: Selects this mod for all NPCs that it provides an appearance for
* Select Available From This Mod: Selects this mod for all NPCs that it provides an appearance for and for which you haven’t already made a selection
* Hide All From This Mod: Hides all mugshots from this mod
* Unhide All From This Mod: Unhides all mugshots from this mod
* Jump To Mod: Switches to the **Mods Menu** and jumps to the selected mod
* Show Full Image: Shows a full screen image of the mugshot (you can also do this with **Ctrl + Rclick**) 
* Share with NPC: Sends the mugshot to another NPC, where you can use its appearance as a replacer. **Click Share and Select** to apply the appearance, or **Share** to make the mugshot available without yet applying it.



<img width="764" height="474" alt="20 Appearance Target Revised" src="https://github.com/user-attachments/assets/c3736a62-2b55-46df-8a97-9641540b8c00" />


<img width="2168" height="626" alt="21 Appearance Share" src="https://github.com/user-attachments/assets/f35a9bce-491d-49fe-957b-48dc7f37ec7b" />


**Symbology**




<img width="750" height="750" alt="No Mugshot" src="https://github.com/user-attachments/assets/6c9a3651-a8a1-47c2-b7e4-f490f05235a2" />


You don’t have a mugshot (for this appearance replacer, for this NPC).



<img width="1024" height="1024" alt="No Associated Data" src="https://github.com/user-attachments/assets/634dec12-9f51-4fb1-84f5-1347b172abed" />



You don’t have the mod installed (but you have the Mugshot for it)



<img width="1024" height="1024" alt="Multiple Plugins for NPC" src="https://github.com/user-attachments/assets/94967557-959c-4914-ab2d-337c9d4bac90" />



This mod contains multiple plugins that contain this NPC. Right click on the symbol to select which one you want to use:


<img width="2200" height="964" alt="22 Source Plugin" src="https://github.com/user-attachments/assets/2ceb383c-cefe-4aa0-b0bc-b9914468ed8f" />




<img width="1024" height="1024" alt="Guest Plugin" src="https://github.com/user-attachments/assets/78246788-0521-4fa2-b3d1-7e114d8a1167" />




This mugshot was shared from another NPC.




<img width="144" height="162" alt="Exclamation" src="https://github.com/user-attachments/assets/c8c336df-9402-4aef-812f-a40cd36695a5" />


There is a notification about this mugshot. Hover your mouse over it to see details.


### Drag And Drop Actions

**Drag Mugshot onto Placeholder**: Assigns the mugshot to the placeholder (applies for all NPCs, not just the current one).



![Dragon Drop](https://github.com/user-attachments/assets/5be1db63-f373-4974-bfad-922cc5ef3b7e)



**Drag Mugshot onto another Mugshot**: For all NPCs where the <span style="text-decoration:underline;">drop target</span> mod is currently selected, replaces the selection with the <span style="text-decoration:underline;">dropped</span> mod (if the NPC is provided). E.g. the dropped mod “gives the old mod the boot”. 



![Mod Boot](https://github.com/user-attachments/assets/17bd6507-e24f-4556-bcda-58c89d4eda64)




## Mods Menu

Here you’ll find batch settings to apply to each mod, as well as see all the NPCs each mod contains.



<img width="2194" height="1184" alt="23 Mods Menu" src="https://github.com/user-attachments/assets/43aab568-6b82-45ff-84d8-0594d61034ed" />



**Border Color**: Indicates the status of the mod.



* Green: Data is available and Mugshots are linked.
* Purple: Data is available but you don’t have mugshots for it (you can still use it for patching).
* Orange: Data is not available, but you have mugshots for it (you can select it as a placeholder but the patcher will skip any NPCs that you select it for).
* Grey: No Data and No Mugshots (only appears when you’ve manually deleted the Mugshot and Corresponding Mod folders). A red **Delete** button will appear in the bottom right corner, allowing you to delete the mod.

**Mod Name**: Click on the mod name to load its mugshots in the right panel (this may take a while for large mods).

**Mugshot Folder**: The path of the folder where mugshots can be found for this mod (note: you can also set this via drag-and-drop; see above).

**Corresponding Mod Folder Paths**: The folder(s) where resources (plugins, textures, meshes) for this mod are located. By default, it contains only the folder where the plugin and/or FaceGen data are. 



* If there’s a separate mod required as a resource (e.g. High Poly Head, Better Argonian Horns, etc) and you want to merge it in rather than leaving it as a master, **Add** it as a Corresponding Mod Folder in addition to the main mod directory.

**Merge In Dependency Records**: Controls if the records that the NPC record references get merged into the output plugin. If unchecked, it’s likely that the source mod will remain as a master. 



* You want to leave this unchecked if the given mod is one where the NPC is defined. For example, if I have Legacy of the Dragonborn installed, and a few LotD appearance replacers, but for some NPCs I want to forward their original appearance, I can do that by selecting their base mod in the **NPCs Menu**, but I’ll uncheck the **Merge In **box in the Mod setting. Otherwise the patcher will try to merge a big chunk of LotD into the output plugin, which may cause it to crash. Mods that are purely appearance replacers are fine to merge in.

**Record Override Handling Mode**: Controls how (non-NPC) override records from this mod are handled. See the explanation above in the **Settings Menu** section. 



* You’ll almost always want to set this to Ignore. Only handle overrides if you see an NPC has a bugged appearance.

**Include Outfits**: Controls whether or not the NPC’s outfit is included in the patcher output (this can also be controlled from the NPCs menu).


### Symbology



<img width="1024" height="1024" alt="Contains Overrides" src="https://github.com/user-attachments/assets/6fa70a2e-f68e-41a0-96aa-5b4306a04bc7" />



Mod contains non-appearance override records. This does not necessarily mean you should handle its overrides. If it’s a base mod (e.g. the base game, Legacy of the Dragonborn, etc), you 100% do not want to handle its overrides as this is likely to crash the patcher or at least slow it down tremendously. However, if it’s a simple NPC replacer and you see the NPCs look bugged in-game, you may want to consider handling overrides for mods marked with this symbol.


## Run Menu

Here you’ll find the controls to actually run the patcher and create output.



<img width="2192" height="1178" alt="24 Run Menu" src="https://github.com/user-attachments/assets/176588ea-0161-4d87-8521-ad4c51dbc3ba" />



**Run Patch Generation**: Apply your selections to create the output .esp file and associated resources in your selected output folder.

**Patch NPCs In Group**: Select which group you want to patch or write .bat files for. You can either select the All NPCs group, or one of the groups you defined in the **NPCs Menu**.

**Generate Spawn Bat**: Generates a .bat file to spawn the NPCs in your currently selected group.

**Verbose Logging**: Generates a verbose log (dug). I only recommend this for troubleshooting.

**Environment Status**: Prints details about your current patcher configuration and load order. If submitting a help request, please copy the output into a PasteBin doc and send me the link.


# FAQ

**Q: Where do I get Mugshots?**

A: I like [Natural Lighting Mugshots](https://www.nexusmods.com/skyrimspecialedition/mods/97595) the best. Other sources include [EasyNPC Mugshot Collection](https://www.nexusmods.com/skyrimspecialedition/mods/99546), [Modpocalypse mugshots](https://www.nexusmods.com/skyrimspecialedition/mods/103837), and the EasyNPC Discord channel.

**Q: How do I migrate from/to EasyNPC**

A: Go to the Settings tab and scroll down to the EasyNPC Transfer section, and click either the Import or Export button.

**Q: For EasyNPC Export, what’s the difference between Export to New vs. Update Existing?**

A: Export To New will assign new Default Plugins based on your load order, while Update Existing will keep the ones in the file that you’re updating and only change the appearance plugin.

**Q: What’s the difference between “Create” and “Create & Patch”?**

A: *Create* splices together your selections into a mod, but doesn’t consider your load order - the resulting plugin will look exactly as if you took the NPCs from your selected mods and stitched them together. You then have to do the conflict patching yourself, or use Synthesis FaceFixer to do it automatically. *Create and Patch* does both things for you, and actually patches your load order to use your selected appearance mods (similar to EasyNPC). In both cases, the N.P.C.2 output assets (textures and meshes) should win all conflicts, but in *Create* mode the plugin should go as high as possible in your load order (and then you or FaceFixer patch the conflicts) while in *Create and Patch* the generated file should only be overwritten by other patcher outputs.

**Q: What’s “Record Override Handling Mode” and when should I use it?**

A: A very small number of appearance mods make changes within records that aren’t typically associated with appearance replacers (e.g. modifying Race records), and don’t work correctly without these modifications. If you set the handling mode to “Include” or “Include as New”, N.P.C.2 will forward these changes into the output. Doing so for all NPCs slows down the patcher quite a bit (and in some cases can crash it) so only use this for the plugins that need it. An example of such a plugin is RS Children.

**Q: For the Handling Mode, what’s the difference between “Include” vs. “Include as New”?**

A: When record overrides are detected, the patcher will handle them in one of three ways:



1. If the Handling Mode is Include As New, the override records will be copied into new records, and any references to them from the NPC will be remapped to these new records. This avoids conflicts with other mods that might reference those records, but by the same token you lose the effects of any mods that touched the original record.
2. If Handling Mode is “Include” and Patching Mode is “Create and Patch”, the patcher will compare the override record to its base record, note the changes, and patch them into your conflict-winning version of that record (this is similar in a way to how Mator Smash works). Note that this is an automated and unsupervised process, so you may want to check the output in xEdit and make sure everything was handled correctly.
3. If Handling Mode is “Include” and Patching Mode is “Create”, the overrides will just be copied in directly from the source mods. Conflict resolution with other mods that touch these records will be on you.

	

The option to choose depends on your needs. If you’re comfortable in xEdit, the safest option is always “Create” because then you just do the conflict resolution yourself, but obviously that’ll get tedious to do whenever you rebuild an NPC merge. “Include” mode is good if you’re comfortable enough in xEdit to take a quick look and make sure the changes forwarded from the appearance mod make sense. “Include as new” may be necessary if you have multiple appearance mod that override the same record in different ways - this will allow you to separate them and use both versions of that record.

As a reminder, this is getting in the weeds and for the vast majority of appearance replacers, you shouldn’t need to override non-appearance records at all.

**Q: Why is NPC2 connecting to the internet? Is this spyware?**

A: This is the Description Provider connecting to UESP or The Elder Scrolls Wiki to source the description of the NPC you’re looking at. You can disable this by unchecking “Show NPC Descriptions”.

**Q: How do NPC Descriptions work for mod-added NPCs that aren’t in UESP?**

A: By default, N.P.C.2 just won’t show descriptions for those NPCs. If you want to add them, you can go the the DescriptionOverrides folder (found in the same folder as the N.P.C.2 .exe file) and create a .json file containing descriptions for the new NPCs. You can find an example in the ExampleOverride.json file in that directory.

**Q: Can N.P.C.2 handle facegen-only mods like Nordic Faces that don’t come with a plugin?**

A:** **Yes. It’ll use the source plugin for those NPCs for conflict patching.

**Q: Why can’t I see {some NPC} from {some mod}?**

A: Check the Rejected NPCs subfolder (next to the main .exe file). It contains logs for all discarded NPCs.

**Q: My appearance mod is mastered to another mod. How do I get that mod to also be merged into the output patch rather than the output patch being mastered to it?**

A: Go to the Mods tab, find your mod, and add the master mod to its Data Folders.

**Q: N.P.C.2 is deciding that my mod is not an appearance mod. Why?**

A: Either it is missing a master and thus can’t be analyzed, or it doesn’t add any new NPCs or modify facial features of existing ones. If it’s missing a master, go to the Settings tab, scroll down to the Non-Apperance Mods list, find your mod, and remove it. Then close N.P.C.2, enable the missing master, and relaunch. 

**Q: My NPC Groups dropdown box is empty. How do I add a group?**

A: Type your desired group name in the box and click Add Current NPCs or Add Visible NPCs to get it started with either your current selected NPC or all NPCs visible in the sidebar with your current filter settings.

**Q: I’m trying to add some mod (say, Children of the Hist) and I’m getting missing masters error for a mod that I don’t have in my load order (example: Better Argonian Horns). What do I do?**

A: Children of the Hist needs Better Argonian Horns as a master. If you for some reason don’t want Better Argonian Horns in your load order, install it, keep it disabled, and add the installation folder as one of the data folders for Children of the Hist.

**Q: Can you add X feature?**

A: Feel free to pitch ideas, but know that I’ll be pretty selective about what I implement, and I’m likely to only act on low-hanging fruit. I thought this project would only take a month but it took 5, and I’m now on a very short countdown before Kid #2 arrives. I’d like to actually enjoy a bit of gaming in my evenings before my free time completely disappears.

**Q: Why is this tagged as AI generated?**

A: Because I used LLMs (mostly Gemini, and also ChatGPT) to write some of the code.

**Q: So is this all AI Slop? Can I trust it?**

A: I primarily used the AI to make the UI, because that part of the process requires a lot of boilerplate code and isn’t particularly fun. For the part that actually interacts with Skyrim data, I took my code from N.P.C.1 and fed it to Gemini, asking to hook it up to the buttons and new data structure from N.P.C.2. I ended up having to rewrite the vast majority of that code myself, and checked over all of it. If there are any bugs attributable to using an LLM, they’ll be in the UI - any bugs in the Skyrim-facing code are of my own making.

**Q: What are the permissions?**

A: Open permissions to the extent allowed by the tools that were used to make this. If you want to modify it, I’d appreciate a Git PR just to keep things centralized and avoid having multiple branches but if you message me and I don’t response for more than a couple weeks, assume I’ve moved on and feel free to launch your own fork.

**Q: Do you accept donations?**

A: Nope, this is a passion project. If you would otherwise send a donation my way, please consider instead supporting humanitarian organizations in Ukraine such as [United24](https://u24.gov.ua/donate), which is where I would forward the money anyway.

**Acknowledgements**



* Noggog for creating Mutagen and teaching me how to use it
* Focustense for creating EasyNPC and providing inspiration
* Istenno for letting me use his Natural Lighting Mugshots to make the logo
* Gemini for assistance coding.

**So, how was the vibe coding?**

Good and bad. LLMs have definitely come a long was since the launch of ChatGPT. There’s very much a tier list. I only used ChatGPT and Gemini for this project (I have paid subscriptions to both) and I confidently say that for managing anything more than the simplest program, Gemini is far superior due to its massive context window. ChatGPT can barely remember multiple functions, while Gemini can consider a large multi-file project. ChatGPT is a bit better at troubleshooting specific functionalities because it’s more to the point; Gemini likes to add its own twist on things (unasked) and strips out comments, and sometimes ignores direct requests, but for large projects ChatGPT just doesn’t have the context window (as of August 1 2025) to analyze and provide helpful output.

Overall, Gemini was great for coding the UI and more or less did exactly what I asked. There was one functionality (the mugshot drag and drop) for which it provided the wrong strategy and I spent 4 or 5 nights troubleshooting/arguing with it before giving up, thinking it through, and fixing it myself using an alternate strategy. Gemini was about to have me reinstall my OS because it was convinced I had a corrupted system. Otherwise, UI coding was largely flawless.

On the back end, where the coding was more specialized and required knowledge of the Mutagen API for Bethesda games, the changes Gemini made to my N.P.C.1 code were largely destructive, sometimes in ways that I didn’t realize until I started looking through its code in fine detail. Its code would usually compile with few or no fixes, but the changes it made resulted in horrible things like patching runs that took 4 hours, or startup initialization that ate 19 GB of RAM. Furthermore, once these behaviors were pointed out, the LLMs were pretty useless at fixing the behavior - every time they would suggest “this is happening because of X; here’s how to fix it…” and their fix would be completely wrong, so I’d always end up having to investigate and fix myself. In retrospect I could have saved at least a month by ditching the LLMs and coding the back end by hand (which, by the end, is largely what happened anyway).

Therefore, while the tools were genuinely helpful (and I don’t think I would have ever launched the project without their help, as after SynthEBD I never want to code another UI unassisted), I really don’t buy the hype about how they’re going to put most programmers out of work. They’re a productivity aid, not a replacement.
