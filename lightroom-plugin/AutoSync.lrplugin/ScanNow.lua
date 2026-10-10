local LrDialogs = import 'LrDialogs'
local LrPrefs = import 'LrPrefs'

LrPrefs.prefsForPlugin().scanNow = true
LrDialogs.showBezel('Checking for new photos…')
