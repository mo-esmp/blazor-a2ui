// Gives the server the browser's time zone for the sample catalog's getTimeZone function.
window.blazorA2UISample = {
    timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone
};
