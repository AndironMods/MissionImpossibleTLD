\## v1.4.0

* Remove background icon on the right side in Quest GUI
* Update GearLookup.json to be more balanced
* Add achievement rewards: unlocking a Steam achievement (base game and DLC) gives a reward, configured at the end of GearLookup.json
* Quest data is now stored inside the save game with ModData (ModData is required): every save has its own quests, loading an older save restores its quests
* New messages: "New daily quests created!", "All daily quests completed!" (also weekly/monthly)
* Performance: rewards are handed out over several frames, item names are looked up without creating items, the journal only rebuilds its texts when something changed



\## v1.3.0

* All Gear Item Names will now be fully localized in all languages



\## v1.2.0

* DLC Content will now be autodetected and enabled



\## v1.1.0

* Add DLC Gear Items/Rewards to GearLookup.json
* Quest objective GUI now shows the friendly JSON key name like ("LilysPancakes") instead of the stripped spawn name ("CookedPancakePeach").
* Quest rewards notification show now the friendly JSON key name like ("LilysPancakes") instead of the stripped spawn name ("GEAR\_CookedPancakePeach").
* Correct wrong prefab Names in GearLookup.json



\## v1.0.0

* Initial release

