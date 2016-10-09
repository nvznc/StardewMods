﻿using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json;
using Pathoschild.LookupAnything.Common;
using Pathoschild.LookupAnything.Components;
using Pathoschild.LookupAnything.Framework;
using Pathoschild.LookupAnything.Framework.Logging;
using Pathoschild.LookupAnything.Framework.Subjects;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace Pathoschild.LookupAnything
{
    /// <summary>The mod entry point.</summary>
    public class LookupAnythingMod : Mod
    {
        /*********
        ** Properties
        *********/
        /****
        ** Configuration
        ****/
        /// <summary>The mod configuration.</summary>
        private ModConfig Config;

        /// <summary>Provides metadata that's not available from the game data directly.</summary>
        private Metadata Metadata;

        /// <summary>The name of the file containing data for the <see cref="Metadata"/> field.</summary>
        private readonly string DatabaseFileName = "data.json";

#if TEST_BUILD
        /// <summary>Reloads the <see cref="Metadata"/> when the underlying file changes.</summary>
        private FileSystemWatcher OverrideFileWatcher;
#endif

        /****
        ** Version check
        ****/
        /// <summary>The current semantic version.</summary>
        private string CurrentVersion;

        /// <summary>The newer release to notify the user about.</summary>
        private GitRelease NewRelease;

        /// <summary>Whether the update-available message has been shown since the game started.</summary>
        private bool HasSeenUpdateWarning;

        /****
        ** State
        ****/
        /// <summary>The previous menu shown before the lookup UI was opened.</summary>
        private IClickableMenu PreviousMenu;

        /// <summary>Finds and analyses lookup targets in the world.</summary>
        private TargetFactory TargetFactory;

        /// <summary>Draws debug information to the screen.</summary>
        private DebugInterface DebugInterface;


        /*********
        ** Public methods
        *********/
        /// <summary>Initialise the mod.</summary>
        public override void Entry(params object[] objects)
        {
            using (ICumulativeLog log = new CumulativeLog())
            {
                log.AppendLine("Lookup Anything initialising...");

                // validate version
                log.Append("checking game/API version... ");
                string versionError = GameHelper.ValidateGameVersion();
                if (versionError != null)
                    Log.Error(versionError);
                log.AppendLine("OK!");

                // load config
                log.Append("loading config... ");
                this.Config = new RawModConfig().InitializeConfig(this.BaseConfigPath).GetParsed();
                log.AppendLine(this.Config.DebugLog ? $"loaded: {JsonConvert.SerializeObject(this.Config)}" : "OK!");

                // load database
                log.Append("loading database... ");
                this.LoadMetadata();
#if TEST_BUILD
                this.OverrideFileWatcher = new FileSystemWatcher(this.PathOnDisk, this.DatabaseFileName)
                {
                    EnableRaisingEvents = true
                };
                this.OverrideFileWatcher.Changed += (sender, e) =>
                {
                    this.LoadMetadata();
                    this.TargetFactory = new TargetFactory(this.Metadata);
                    this.DebugInterface = new DebugInterface(this.TargetFactory, this.Config)
                    {
                        Enabled = this.DebugInterface.Enabled
                    };
                };
#endif
                log.AppendLine("OK!");

                // initialise functionality
                log.Append("initialising framework... ");
                this.CurrentVersion = UpdateHelper.GetSemanticVersion(this.Manifest.Version);
                this.TargetFactory = new TargetFactory(this.Metadata);
                this.DebugInterface = new DebugInterface(this.TargetFactory, this.Config);
                log.AppendLine("OK!");

                // hook up events
                log.Append("registering event listeners... ");
                {
                    // reset low-level cache once per game day (used for expensive queries that don't change within a day)
                    PlayerEvents.LoadedGame += (sender, e) => GameHelper.ResetCache(this.Metadata);
                    TimeEvents.OnNewDay += (sender, e) => GameHelper.ResetCache(this.Metadata);
                    
                    // hook up game events
                    GameEvents.GameLoaded += (sender, e) => this.ReceiveGameLoaded();
                    GraphicsEvents.OnPostRenderHudEvent += (sender, e) => this.ReceiveInterfaceRendering(Game1.spriteBatch);
                    MenuEvents.MenuClosed += (sender, e) => this.ReceiveMenuClosed(e.PriorMenu);

                    // hook up keyboard
                    if (this.Config.Keyboard.HasAny())
                    {
                        ControlEvents.KeyPressed += (sender, e) => this.ReceiveKeyPress(e.KeyPressed, this.Config.Keyboard);
                        if (this.Config.HideOnKeyUp)
                            ControlEvents.KeyReleased += (sender, e) => this.ReceiveKeyRelease(e.KeyPressed, this.Config.Keyboard);
                    }

                    // hook up controller
                    if (this.Config.Controller.HasAny())
                    {
                        ControlEvents.ControllerButtonPressed += (sender, e) => this.ReceiveKeyPress(e.ButtonPressed, this.Config.Controller);
                        ControlEvents.ControllerTriggerPressed += (sender, e) => this.ReceiveKeyPress(e.ButtonPressed, this.Config.Controller);
                        if (this.Config.HideOnKeyUp)
                        {
                            ControlEvents.ControllerButtonReleased += (sender, e) => this.ReceiveKeyRelease(e.ButtonReleased, this.Config.Controller);
                            ControlEvents.ControllerTriggerReleased += (sender, e) => this.ReceiveKeyRelease(e.ButtonReleased, this.Config.Controller);
                        }
                    }
                }
                log.AppendLine("OK!");

                log.AppendLine(this.Config.DebugLog ? "ready!" : "ready! further logging disabled by config.");
            }
        }


        /*********
        ** Private methods
        *********/
        /****
        ** Event handlers
        ****/
        /// <summary>The method invoked when the player loads the game.</summary>
        private void ReceiveGameLoaded()
        {
            // check for an updated version
            if (this.Config.CheckForUpdates)
            {
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        using (ICumulativeLog log = this.GetTaskLog())
                        {
                            log.Append("Lookup Anything checking for update... ");

                            GitRelease release = UpdateHelper.GetLatestReleaseAsync("Pathoschild/LookupAnything").Result;
                            if (release.IsNewerThan(this.CurrentVersion))
                            {
                                log.AppendLine("update to version {release.Name} available.");
                                this.NewRelease = release;
                            }
                            else
                                log.AppendLine("no update available.");
                        }
                    }
                    catch (Exception ex)
                    {
                        this.HandleError(ex, "checking for a newer version");
                    }
                });
            }
        }

        /// <summary>The method invoked when the player presses an input button.</summary>
        /// <typeparam name="TKey">The input type.</typeparam>
        /// <param name="key">The pressed input.</param>
        /// <param name="map">The configured input mapping.</param>
        private void ReceiveKeyPress<TKey>(TKey key, InputMapConfiguration<TKey> map)
        {
            if (!map.IsValidKey(key))
                return;

            try
            {
                // perform bound action
                if (key.Equals(map.ToggleLookup))
                    this.ToggleLookup();
                if (key.Equals(map.ScrollUp))
                    (Game1.activeClickableMenu as LookupMenu)?.ScrollUp(this.Config.ScrollAmount);
                else if (key.Equals(map.ScrollDown))
                    (Game1.activeClickableMenu as LookupMenu)?.ScrollDown(this.Config.ScrollAmount);
                else if (key.Equals(map.ToggleDebug))
                    this.DebugInterface.Enabled = !this.DebugInterface.Enabled;
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "handling your input", $"handling input '{key}'.");
            }
        }

        /// <summary>The method invoked when the player presses an input button.</summary>
        /// <typeparam name="TKey">The input type.</typeparam>
        /// <param name="key">The pressed input.</param>
        /// <param name="map">The configured input mapping.</param>
        private void ReceiveKeyRelease<TKey>(TKey key, InputMapConfiguration<TKey> map)
        {
            if (!map.IsValidKey(key))
                return;

            try
            {
                if (key.Equals(map.ToggleLookup))
                    this.HideLookup();
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "handling your input", $"handling input '{key}'.");
            }
        }

        /// <summary>The method invoked when the player closes a displayed menu.</summary>
        /// <param name="closedMenu">The menu which the player just closed.</param>
        private void ReceiveMenuClosed(IClickableMenu closedMenu)
        {
            try
            {
                // restore the previous menu if it was hidden to display the lookup UI.
                if (closedMenu is LookupMenu && this.PreviousMenu != null)
                {
                    Game1.activeClickableMenu = this.PreviousMenu;
                    this.PreviousMenu = null;
                }
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "restoring the previous menu");
            }
        }

        /// <summary>The method invoked when the interface is rendering.</summary>
        /// <param name="spriteBatch">The sprite batch being drawn.</param>
        private void ReceiveInterfaceRendering(SpriteBatch spriteBatch)
        {
            // render debug interface
            if (this.DebugInterface.Enabled)
            {
                try
                {
                    this.DebugInterface.Draw(spriteBatch);
                }
                catch (Exception ex)
                {
                    GameHelper.ShowErrorMessage("Huh. Something went wrong drawing the debug info. The game error log has the technical details.");
                    Log.Error(ex.ToString());
                }
            }

            // render update warning
            if (this.Config.CheckForUpdates && !this.HasSeenUpdateWarning && this.NewRelease != null)
            {
                this.HasSeenUpdateWarning = true;
                GameHelper.ShowInfoMessage($"You can update Lookup Anything from {this.CurrentVersion} to {this.NewRelease.Version}.");
            }
        }

        /****
        ** Helpers
        ****/
        /// <summary>Show the lookup UI for the current target.</summary>
        private void ToggleLookup()
        {
            if (Game1.activeClickableMenu is LookupMenu)
                this.HideLookup();
            else
                this.ShowLookup();
        }

        /// <summary>Show the lookup UI for the current target.</summary>
        private void ShowLookup()
        {
            using (ICumulativeLog log = this.GetTaskLog())
            {
                log.Append("Lookup Anything received a lookup request. ");

                try
                {
                    // validate version
                    string versionError = GameHelper.ValidateGameVersion();
                    if (versionError != null)
                    {
                        GameHelper.ShowErrorMessage(versionError);
                        Log.Error(versionError);
                        return;
                    }

                    // get target
                    ISubject subject = null;
                    if (Game1.activeClickableMenu != null)
                    {
                        log.Append($"Searching the open '{Game1.activeClickableMenu.GetType().Name}' menu... ");
                        subject = this.TargetFactory.GetSubjectFrom(Game1.activeClickableMenu, GameHelper.GetScreenCoordinatesFromCursor());
                    }
                    else
                    {
                        log.Append("Searching the world... ");
                        subject = this.TargetFactory.GetSubjectFrom(Game1.currentLocation, Game1.currentCursorTile, GameHelper.GetScreenCoordinatesFromCursor());
                    }
                    if (subject == null)
                    {
                        log.AppendLine("no target found.");
                        return;
                    }


                    // show lookup UI
                    log.AppendLine($"showing {subject.GetType().Name}::{subject.Type}::{subject.Name}.");
                    this.PreviousMenu = Game1.activeClickableMenu;
                    Game1.activeClickableMenu = new LookupMenu(subject, this.Metadata);
                }
                catch (Exception ex)
                {
                    this.HandleError(ex, "looking that up");
                }
            }
        }

        /// <summary>Show the lookup UI for the current target.</summary>
        private void HideLookup()
        {
            try
            {
                if (Game1.activeClickableMenu is LookupMenu)
                {
                    Game1.playSound("bigDeSelect"); // match default behaviour when closing a menu
                    Game1.activeClickableMenu = null;
                }
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "closing the menu");
            }
        }

        /// <summary>Load the file containing metadata that's not available from the game directly.</summary>
        private void LoadMetadata()
        {
            try
            {
                string content = File.ReadAllText(Path.Combine(this.PathOnDisk, this.DatabaseFileName));
                this.Metadata = JsonConvert.DeserializeObject<Metadata>(content);
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "loading metadata");
            }
        }

        /// <summary>Log an error and warn the user.</summary>
        /// <param name="ex">The exception to handle.</param>
        /// <param name="verb">The verb describing where the error occurred (e.g. "looking that up"). This is displayed on the screen, so it should be simple and avoid characters that might not be available in the sprite font.</param>
        /// <param name="detailedVerb">A more detailed form of <see cref="verb"/> if applicable. This is displayed in the log, so it can be more technical and isn't constrained by the sprite font.</param>
        private void HandleError(Exception ex, string verb, string detailedVerb = null)
        {
            detailedVerb = detailedVerb ?? verb;
            Log.Error($"[Lookup Anything] Something went wrong {detailedVerb}:{Environment.NewLine}{ex}");
            GameHelper.ShowErrorMessage($"Huh. Something went wrong {verb}. The game error log has the technical details.");
        }

        /// <summary>Get a logger which collects messages for a discrete task and logs them as one entry when disposed.</summary>
        private ICumulativeLog GetTaskLog()
        {
            if(!this.Config.DebugLog)
                return new DisabledLog();
            return new CumulativeLog();
        }
    }
}